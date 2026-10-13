"""The service contract, and that the model behaves like dispersion rather than like a drawing.

The C# side pins the same JSON shape in PlumeContractTests.
"""

import copy

import pytest
from fastapi.testclient import TestClient
from shapely.geometry import Point, Polygon

from plume_service import dispersion
from plume_service.main import app

client = TestClient(app)

RELEASE_LAT, RELEASE_LON = 53.35, -6.26

REQUEST = {
    "releaseLocation": {"latitude": RELEASE_LAT, "longitude": RELEASE_LON},
    "substance": "chlorine",
    "releaseRateKgPerSecond": 2.0,
    "durationMinutes": 30,
    "weather": {
        "windFromDegrees": 270,  # westerly wind, plume heads east
        "windSpeedMps": 4,
        "temperatureC": 14,
        "relativeHumidity": 0.7,
        "stabilityClass": "D",
    },
}


def predict(**overrides):
    """A prediction for the standard request with `overrides` applied (nested dicts merged)."""
    body = copy.deepcopy(REQUEST)
    for key, value in overrides.items():
        if isinstance(value, dict) and isinstance(body.get(key), dict):
            body[key].update(value)
        else:
            body[key] = value
    response = client.post("/v1/plume/predict", json=body)
    assert response.status_code == 200, response.text
    return response.json()


def polygon(zone) -> Polygon:
    return Polygon([(p["longitude"], p["latitude"]) for p in zone["boundary"]])


def zones_by_level(body) -> dict[str, dict]:
    return {z["level"]: z for z in body["zones"]}


# ---- The contract ----

def test_health_names_the_model():
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert body["plumeModel"] == dispersion.MODEL_NAME


def test_predict_returns_camel_case_contract():
    body = predict()
    assert {"model", "generatedAt", "zones", "substance", "frontMeters", "denseGas"} <= set(body)
    assert [z["level"] for z in body["zones"]] == ["high", "moderate", "low"]
    zone = body["zones"][0]
    assert {"level", "boundary", "thresholdMgPerM3", "areaSquareMeters", "maxDistanceMeters", "arrivalSeconds"} <= set(zone)
    assert body["substance"]["materialClass"] == "chemical"
    assert body["substance"]["lowMgPerM3"] < body["substance"]["highMgPerM3"]


def test_the_phase_0_request_still_works_without_the_new_fields():
    """Only the original five fields: the rest take defaults."""
    assert predict()["zones"], "a bare request should still get a prediction"


def test_rejects_invalid_input():
    assert client.post("/v1/plume/predict", json={**REQUEST, "releaseRateKgPerSecond": -1}).status_code == 422
    assert client.post("/v1/plume/predict", json={**REQUEST, "weather": {**REQUEST["weather"], "stabilityClass": "Z"}}).status_code == 422


def test_substances_endpoint_lists_every_material_class():
    body = client.get("/v1/substances").json()
    classes = {s["materialClass"] for s in body}
    assert classes == {"chemical", "smoke", "toxic-gas", "biological", "radiological"}
    assert all(s["advisory"] for s in body)


# ---- Geometry ----

def test_zones_are_nested_and_start_at_the_release():
    zones = [polygon(z) for z in predict()["zones"]]
    high, moderate, low = zones
    assert all(z.is_valid for z in zones)
    assert high.area < moderate.area < low.area
    # Each band lies inside the weaker one around it (a metre of slack for the sampled edges).
    assert low.buffer(1e-5).contains(moderate)
    assert moderate.buffer(1e-5).contains(high)
    # While the release continues, the cloud starts at the source.
    assert low.buffer(1e-4).contains(Point(RELEASE_LON, RELEASE_LAT))


def test_plume_travels_downwind_and_turns_with_the_wind():
    easterly = polygon(zones_by_level(predict())["low"])
    assert easterly.centroid.x > RELEASE_LON  # westerly wind: the cloud goes east

    northerly = polygon(zones_by_level(predict(weather={"windFromDegrees": 180}))["low"])
    assert northerly.centroid.y > RELEASE_LAT  # southerly wind: it goes north
    assert abs(northerly.centroid.x - RELEASE_LON) < abs(easterly.centroid.x - RELEASE_LON)


def test_zones_report_their_threshold_area_and_arrival():
    zones = zones_by_level(predict())
    assert zones["high"]["thresholdMgPerM3"] > zones["low"]["thresholdMgPerM3"]
    assert zones["low"]["areaSquareMeters"] > zones["high"]["areaSquareMeters"] > 0
    assert zones["low"]["maxDistanceMeters"] > zones["high"]["maxDistanceMeters"] > 0
    # 4 m/s wind: the far edge of the low zone is reached in distance / speed seconds.
    assert zones["low"]["arrivalSeconds"] >= 0


# ---- Dispersion behaviour ----

def test_stable_air_carries_a_plume_further_than_unstable_air():
    """F: a narrow ribbon for kilometres. A: mixed out within a few hundred metres."""
    stable = zones_by_level(predict(weather={"stabilityClass": "F"}))["moderate"]
    unstable = zones_by_level(predict(weather={"stabilityClass": "A"}))["moderate"]
    assert stable["maxDistanceMeters"] > unstable["maxDistanceMeters"] * 2


def test_a_bigger_release_reaches_further():
    small = zones_by_level(predict(releaseRateKgPerSecond=0.2))["moderate"]["maxDistanceMeters"]
    large = zones_by_level(predict(releaseRateKgPerSecond=20.0))["moderate"]["maxDistanceMeters"]
    assert large > small


def test_a_stronger_wind_dilutes_the_cloud():
    """Doubling the wind halves the concentration at a given distance, so the zones shrink."""
    light = zones_by_level(predict(weather={"windSpeedMps": 2}))["high"]["areaSquareMeters"]
    strong = zones_by_level(predict(weather={"windSpeedMps": 12}))["high"]["areaSquareMeters"]
    assert strong < light


def test_built_up_ground_mixes_a_plume_out_sooner_than_open_country():
    urban = predict(terrain={"roughnessMeters": 1.5})
    rural = predict(terrain={"roughnessMeters": 0.03})
    assert urban["urban"] is True and rural["urban"] is False
    assert (
        zones_by_level(urban)["moderate"]["maxDistanceMeters"]
        < zones_by_level(rural)["moderate"]["maxDistanceMeters"]
    )


# ---- Material behaviour ----

def test_chlorine_is_a_dense_gas_that_stays_on_the_ground():
    body = predict(substance="chlorine")
    assert body["denseGas"] is True
    assert body["plumeRiseMeters"] == 0.0
    assert body["effectiveHeightMeters"] == 0.0


def test_hot_smoke_lifts_so_the_worst_ground_level_is_downwind_of_the_fire():
    body = predict(substance="smoke", releaseTemperatureC=400, releaseRateKgPerSecond=5.0)
    assert body["denseGas"] is False
    assert body["plumeRiseMeters"] > 0
    assert body["effectiveHeightMeters"] > 0

    # An elevated plume is weakest at the source and strongest some way downwind.
    plume = _plume(substance="smoke", release_temperature=400, rate=5.0)
    near = plume.centreline(50)
    peak = max(plume.centreline(x) for x in range(100, 4000, 50))
    assert peak > near


def test_ammonia_is_lighter_than_air_and_rises_even_when_cold():
    body = predict(substance="ammonia", releaseRateKgPerSecond=1.0)
    assert body["denseGas"] is False
    assert body["plumeRiseMeters"] > 0


def test_radioactive_material_reports_dose_rates():
    zones = zones_by_level(predict(substance="caesium-137 particulate"))
    assert all(z["doseRateMsvPerHour"] is not None for z in zones.values())
    assert zones["high"]["doseRateMsvPerHour"] > zones["low"]["doseRateMsvPerHour"]
    assert predict(substance="caesium-137 particulate")["substance"]["materialClass"] == "radiological"
    # A chemical release has no dose to report.
    assert zones_by_level(predict(substance="chlorine"))["high"]["doseRateMsvPerHour"] is None


def test_a_decaying_aerosol_loses_strength_on_the_way():
    """Biological material decays in the air, so a far-off point is weaker than decay-free material."""
    plume = _plume(substance="biological aerosol", rate=0.01)
    assert plume.substance.half_life_minutes
    # Two hours' travel at 4 m/s is about 29 km: one half-life, so roughly half the source strength.
    far = plume.travel_seconds(28_800) / 60
    assert far == pytest.approx(plume.substance.half_life_minutes, rel=0.1)
    decayed = plume._source_strength(28_800, 1.0) / (plume.release.rate_kg_per_second * 1e6)
    assert 0.4 < decayed < 0.6


def test_an_unidentified_substance_is_treated_as_a_toxic_gas_but_keeps_its_name():
    body = predict(substance="unknown yellow vapour")
    assert body["substance"]["materialClass"] == "toxic-gas"
    assert body["substance"]["name"] == "unknown yellow vapour"
    assert body["zones"], "an unidentified release still gets zones"


# ---- A travelling cloud ----

def test_the_cloud_reaches_only_as_far_as_the_wind_has_carried_it():
    early = predict(elapsedMinutes=2)
    later = predict(elapsedMinutes=20)
    assert early["frontMeters"] == pytest.approx(2 * 60 * 4, rel=0.01)  # 4 m/s for two minutes
    assert later["frontMeters"] > early["frontMeters"]
    assert (
        zones_by_level(later)["low"]["maxDistanceMeters"]
        > zones_by_level(early)["low"]["maxDistanceMeters"]
    )


def test_a_finished_release_detaches_from_the_source_and_drifts_away():
    """Five minutes of release, seen ten minutes in: a band of cloud downwind, and clear air at the scene."""
    body = predict(durationMinutes=5, elapsedMinutes=10)
    assert body["rearMeters"] > 0
    low = polygon(zones_by_level(body)["low"])
    assert not low.contains(Point(RELEASE_LON, RELEASE_LAT))
    assert low.bounds[0] > RELEASE_LON  # the whole band is downwind of the release


def test_the_inventory_caps_how_long_it_can_run():
    """A 60-minute release from a store holding 10 minutes' worth detaches as if it ran for ten."""
    body = predict(durationMinutes=60, quantityKg=1200, releaseRateKgPerSecond=2.0, elapsedMinutes=15)
    assert body["rearMeters"] > 0  # the store emptied after 10 minutes
    assert predict(durationMinutes=60, releaseRateKgPerSecond=2.0, elapsedMinutes=15)["rearMeters"] == 0


# ---- Helper ----

def _plume(substance: str, rate: float = 2.0, release_temperature: float | None = None) -> dispersion.GaussianPlume:
    """The model directly, for assertions about concentration that the wire format does not carry."""
    from plume_service import substances as library

    return dispersion.GaussianPlume(
        origin=(RELEASE_LAT, RELEASE_LON),
        release=dispersion.Release(
            substance=library.find(substance),
            rate_kg_per_second=rate,
            duration_minutes=30,
            temperature_c=release_temperature,
        ),
        conditions=dispersion.Conditions(wind_from_degrees=270, wind_speed_mps=4, temperature_c=14),
        terrain=dispersion.Terrain(),
    )


def test_an_explicit_null_terrain_is_treated_as_no_terrain_given():
    """.NET serialises an omitted optional record as null; it must not be a validation error."""
    assert predict(terrain=None)["zones"]
