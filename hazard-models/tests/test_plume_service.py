from fastapi.testclient import TestClient
from shapely.geometry import Point, Polygon

from plume_service.main import app

client = TestClient(app)

REQUEST = {
    "releaseLocation": {"latitude": 53.35, "longitude": -6.26},
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


def _polygon(zone):
    return Polygon([(p["longitude"], p["latitude"]) for p in zone["boundary"]])


def test_health():
    assert client.get("/health").json()["status"] == "ok"


def test_predict_returns_camel_case_contract():
    body = client.post("/v1/plume/predict", json=REQUEST).json()
    assert set(body) == {"model", "generatedAt", "zones"}
    assert [z["level"] for z in body["zones"]] == ["high", "moderate", "low"]


def test_zones_are_nested_and_contain_release():
    zones = [_polygon(z) for z in client.post("/v1/plume/predict", json=REQUEST).json()["zones"]]
    release = Point(-6.26, 53.35)
    assert all(z.is_valid for z in zones)
    assert zones[0].contains(release)
    assert zones[1].contains(zones[0]) and zones[2].contains(zones[1])


def test_plume_travels_downwind():
    low = _polygon(client.post("/v1/plume/predict", json=REQUEST).json()["zones"][-1])
    assert low.centroid.x > -6.26  # east of the release for a westerly wind


def test_rejects_invalid_input():
    bad = {**REQUEST, "releaseRateKgPerSecond": -1}
    assert client.post("/v1/plume/predict", json=bad).status_code == 422
