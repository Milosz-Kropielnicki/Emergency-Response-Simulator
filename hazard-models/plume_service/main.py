"""Hazard-model HTTP service called by the C# simulation engine.

Run from the hazard-models folder:
    .venv\\Scripts\\python -m uvicorn plume_service.main:app --host 127.0.0.1 --port 8765
"""

from datetime import datetime, timezone

from fastapi import FastAPI

from . import dispersion, substances
from .models import (
    GeoPoint,
    PlumePrediction,
    PlumeRequest,
    PlumeZone,
    SubstanceInfo,
)

app = FastAPI(title="Emergency Response Simulator - Hazard Models", version="1.0.0")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "plumeModel": dispersion.MODEL_NAME}


@app.get("/v1/substances", response_model=list[SubstanceInfo], response_model_by_alias=True)
def list_substances() -> list[SubstanceInfo]:
    """Every substance the model knows, for the instructor's inject dialog."""
    return [_substance_info(s) for s in substances.ALL]


@app.post("/v1/plume/predict", response_model=PlumePrediction, response_model_by_alias=True)
def predict_plume(request: PlumeRequest) -> PlumePrediction:
    """Where this release goes, and how strong it still is when it gets there."""
    substance = substances.find(request.substance)

    plume = dispersion.GaussianPlume(
        origin=(request.release_location.latitude, request.release_location.longitude),
        release=dispersion.Release(
            substance=substance,
            rate_kg_per_second=request.release_rate_kg_per_second,
            duration_minutes=request.duration_minutes,
            quantity_kg=request.quantity_kg,
            temperature_c=request.release_temperature_c,
            pressure_kpa=request.release_pressure_kpa,
            height_meters=request.release_height_meters,
            diameter_meters=request.release_diameter_meters,
            elapsed_minutes=request.elapsed_minutes,
        ),
        conditions=dispersion.Conditions(
            wind_from_degrees=request.weather.wind_from_degrees,
            wind_speed_mps=request.weather.wind_speed_mps,
            temperature_c=request.weather.temperature_c,
            relative_humidity=request.weather.relative_humidity,
            stability_class=request.weather.stability_class,
            pressure_kpa=request.weather.pressure_kpa,
        ),
        terrain=dispersion.Terrain(roughness_meters=request.terrain.roughness_meters) if request.terrain else dispersion.Terrain(),
    )

    prediction = plume.predict()
    return PlumePrediction(
        model=prediction.model,
        generated_at=datetime.now(timezone.utc),
        zones=[_zone(z) for z in prediction.zones],
        substance=_substance_info(prediction.substance),
        front_meters=prediction.front_meters,
        rear_meters=prediction.rear_meters,
        effective_height_meters=prediction.effective_height_meters,
        plume_rise_meters=prediction.plume_rise_meters,
        dense_gas=prediction.dense_gas,
        stability_class=prediction.stability_class,  # type: ignore[arg-type]
        urban=prediction.urban,
    )


def _zone(zone: dispersion.Zone) -> PlumeZone:
    return PlumeZone(
        level=zone.level,  # type: ignore[arg-type]
        boundary=[GeoPoint(latitude=lat, longitude=lon) for lat, lon in zone.boundary],
        threshold_mg_per_m3=zone.threshold_mg_per_m3,
        dose_rate_msv_per_hour=zone.dose_rate_msv_per_hour,
        area_square_meters=zone.area_square_meters,
        max_distance_meters=zone.max_distance_meters,
        arrival_seconds=zone.arrival_seconds,
    )


def _substance_info(substance: substances.Substance) -> SubstanceInfo:
    return SubstanceInfo(
        name=substance.name,
        material_class=substance.material_class,
        low_mg_per_m3=substance.low,
        moderate_mg_per_m3=substance.moderate,
        high_mg_per_m3=substance.high,
        molar_mass=substance.molar_mass,
        half_life_minutes=substance.half_life_minutes,
        advisory=substance.advisory,
    )
