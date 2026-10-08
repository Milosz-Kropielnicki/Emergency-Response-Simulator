"""Wire contract for the plume service.

Mirrors the C# records in Emergency Response Simulator.Core/Contracts/IPlumeService.cs.
JSON uses camelCase on the wire to match .NET's JsonSerializerDefaults.Web.
"""

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class WireModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class GeoPoint(WireModel):
    latitude: float = Field(ge=-90, le=90)
    longitude: float = Field(ge=-180, le=180)


class WeatherInput(WireModel):
    wind_from_degrees: float = Field(ge=0, le=360, description="Direction the wind blows from.")
    wind_speed_mps: float = Field(ge=0)
    temperature_c: float
    relative_humidity: float = Field(ge=0, le=1)
    stability_class: Literal["A", "B", "C", "D", "E", "F"] = "D"


class PlumeRequest(WireModel):
    release_location: GeoPoint
    substance: str
    release_rate_kg_per_second: float = Field(gt=0)
    duration_minutes: float = Field(gt=0)
    weather: WeatherInput


class PlumeZone(WireModel):
    level: Literal["high", "moderate", "low"]
    boundary: list[GeoPoint]


class PlumePrediction(WireModel):
    model: str
    generated_at: datetime
    zones: list[PlumeZone]
