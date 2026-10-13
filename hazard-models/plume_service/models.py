"""Wire contract for the plume service.

Mirrors the C# records in Emergency Response Simulator.Core/Contracts/IPlumeService.cs.
JSON uses camelCase on the wire to match .NET's JsonSerializerDefaults.Web.

Everything added in Phase 13 is optional with a default, so a caller that only knows the Phase 0
contract (substance, rate, duration, weather) still gets a prediction.
"""

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

StabilityClass = Literal["A", "B", "C", "D", "E", "F"]
Level = Literal["high", "moderate", "low"]
MaterialClass = Literal["chemical", "smoke", "toxic-gas", "biological", "radiological"]


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
    stability_class: StabilityClass = "D"
    pressure_kpa: float = Field(default=101.3, gt=0, description="Ambient air pressure.")


class TerrainInput(WireModel):
    """The ground the cloud travels over."""

    roughness_meters: float = Field(
        default=1.0,
        ge=0,
        description="Aerodynamic roughness length: ~0.03 open grass, ~0.5 suburb, >1 city centre. "
        "0.4 and above uses urban dispersion curves.",
    )


class PlumeRequest(WireModel):
    release_location: GeoPoint
    substance: str
    release_rate_kg_per_second: float = Field(gt=0)
    duration_minutes: float = Field(gt=0)
    weather: WeatherInput

    # Phase 13: the rest of the release, all optional.
    quantity_kg: float | None = Field(default=None, gt=0, description="Inventory; caps the duration when it runs out sooner.")
    release_temperature_c: float | None = Field(default=None, description="None: it leaves at ambient temperature.")
    release_pressure_kpa: float | None = Field(default=None, gt=0)
    release_height_meters: float = Field(default=0.0, ge=0, description="Height of the opening above ground.")
    release_diameter_meters: float = Field(default=0.5, gt=0)
    elapsed_minutes: float | None = Field(
        default=None,
        ge=0,
        description="How long it has been running. None predicts the steady state, ignoring how far the cloud has got.",
    )
    terrain: TerrainInput | None = Field(
        default=None,
        description="Omitted, or an explicit null from a caller that has no terrain to give: open-city defaults.",
    )


class PlumeZone(WireModel):
    level: Level
    boundary: list[GeoPoint]

    # Phase 13: what the zone was drawn at and what it means.
    threshold_mg_per_m3: float = 0.0
    dose_rate_msv_per_hour: float | None = Field(default=None, description="Radiological material only.")
    area_square_meters: float = 0.0
    max_distance_meters: float = 0.0
    arrival_seconds: float | None = Field(default=None, description="Time from the release reaching this zone's near edge.")


class SubstanceInfo(WireModel):
    """What the model took the substance to be, and what command should know about it."""

    name: str
    material_class: MaterialClass
    low_mg_per_m3: float
    moderate_mg_per_m3: float
    high_mg_per_m3: float
    molar_mass: float
    half_life_minutes: float | None = None
    advisory: str = ""


class PlumePrediction(WireModel):
    model: str
    generated_at: datetime
    zones: list[PlumeZone]

    # Phase 13: how the model read the release, for the instructor's board and the review.
    substance: SubstanceInfo | None = None
    front_meters: float = 0.0
    rear_meters: float = Field(default=0.0, description="Back of the cloud: non-zero once a finished release detaches from the source.")
    effective_height_meters: float = 0.0
    plume_rise_meters: float = 0.0
    dense_gas: bool = False
    stability_class: StabilityClass = "D"
    urban: bool = False
