"""Hazard-model HTTP service called by the C# simulation engine.

Run from the hazard-models folder:
    .venv\\Scripts\\python -m uvicorn plume_service.main:app --host 127.0.0.1 --port 8765
"""

from datetime import datetime, timezone

from fastapi import FastAPI

from . import stub_model
from .models import PlumePrediction, PlumeRequest

app = FastAPI(title="Emergency Response Simulator - Hazard Models", version="0.1.0")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "plumeModel": stub_model.MODEL_NAME}


@app.post("/v1/plume/predict", response_model=PlumePrediction, response_model_by_alias=True)
def predict_plume(request: PlumeRequest) -> PlumePrediction:
    return PlumePrediction(
        model=stub_model.MODEL_NAME,
        generated_at=datetime.now(timezone.utc),
        zones=stub_model.predict(request),
    )
