"""Atmospheric dispersion: where a release goes and how strong it still is when it gets there.

A Gaussian plume for a continuous ground-level or elevated release, with the pieces that change what
command should do about it:

* **Pasquill-Gifford dispersion** using the Briggs (1973) sigma curves, rural or urban by terrain, for
  stability classes A (very unstable) to F (very stable). Stable air keeps a plume narrow and
  concentrated for kilometres; unstable air mixes it out within a few hundred metres.
* **Plume rise** (Briggs) for a release that leaves its source hot or fast, so smoke lifts over the
  near field and the worst ground-level concentration is some way downwind, not at the fire.
* **Dense-gas behaviour** for anything heavier than air: no rise, a shallower and wider cloud that
  hugs the ground and collects in low places.
* **Depletion**: material settling onto the ground thins the cloud as it travels, and decaying
  material (radiological, biological) loses strength with time in the air.
* **A travelling cloud**: it reaches only as far as the wind has carried it since the release began,
  and once the release stops the back of the cloud moves downwind too, so the cloud can detach from
  the source entirely.

The output is the thing the simulator needs: polygons for the low, moderate and high concentration
zones, each with the concentration it was drawn at, its area, how far downwind it reaches and when it
got there.

A Gaussian plume is the standard screening model and is what a training simulator wants: it is fast,
well understood, and good to a factor of two in flat terrain over open ground. It assumes steady
conditions and flat ground, so it does not route a cloud around buildings or down a valley, and the
dense-gas treatment is an approximation of a proper slumping model (DEGADIS, SLAB). Treat every
number as a prediction, which is how the simulator presents it.
"""

import math
from dataclasses import dataclass, field

from . import substances
from .substances import Substance

MODEL_NAME = "gaussian-plume-v1"

GRAVITY = 9.81

#: Concentration levels, strongest first, and the threshold each is drawn at.
LEVELS = ("high", "moderate", "low")

#: Nothing is predicted beyond this distance from the release.
MAX_RANGE_METERS = 30_000.0

#: Points along each edge of a zone polygon.
_SAMPLES = 40

_METERS_PER_DEGREE_LAT = 111_320.0


# ---- Dispersion coefficients -------------------------------------------------------------------

def _sigma_rural(stability: str, x: float) -> tuple[float, float]:
    """Briggs rural (open-country) sigma_y and sigma_z in metres, for downwind distance x."""
    if stability == "A":
        return 0.22 * x / math.sqrt(1 + 1e-4 * x), 0.20 * x
    if stability == "B":
        return 0.16 * x / math.sqrt(1 + 1e-4 * x), 0.12 * x
    if stability == "C":
        return 0.11 * x / math.sqrt(1 + 1e-4 * x), 0.08 * x / math.sqrt(1 + 2e-4 * x)
    if stability == "D":
        return 0.08 * x / math.sqrt(1 + 1e-4 * x), 0.06 * x / math.sqrt(1 + 1.5e-3 * x)
    if stability == "E":
        return 0.06 * x / math.sqrt(1 + 1e-4 * x), 0.03 * x / (1 + 3e-4 * x)
    return 0.04 * x / math.sqrt(1 + 1e-4 * x), 0.016 * x / (1 + 3e-4 * x)


def _sigma_urban(stability: str, x: float) -> tuple[float, float]:
    """Briggs urban sigma_y and sigma_z in metres: buildings mix a plume faster near the source."""
    if stability in ("A", "B"):
        return 0.32 * x / math.sqrt(1 + 4e-4 * x), 0.24 * x * math.sqrt(1 + 1e-3 * x)
    if stability == "C":
        return 0.22 * x / math.sqrt(1 + 4e-4 * x), 0.20 * x
    if stability == "D":
        return 0.16 * x / math.sqrt(1 + 4e-4 * x), 0.14 * x / math.sqrt(1 + 3e-4 * x)
    return 0.11 * x / math.sqrt(1 + 4e-4 * x), 0.08 * x / math.sqrt(1 + 1.5e-3 * x)


#: Wind-profile exponents for u(z) = u10 (z/10)^p, by stability class over rough ground.
_WIND_EXPONENT = {"A": 0.15, "B": 0.15, "C": 0.20, "D": 0.25, "E": 0.40, "F": 0.60}


@dataclass(frozen=True)
class Terrain:
    """The ground the cloud travels over."""

    roughness_meters: float = 1.0
    """Aerodynamic roughness length: ~0.03 open grass, ~0.5 suburb, >1 city centre."""

    @property
    def urban(self) -> bool:
        """Urban dispersion curves apply over built-up ground."""
        return self.roughness_meters >= 0.4

    def sigmas(self, stability: str, x: float) -> tuple[float, float]:
        x = max(x, 1.0)
        sy, sz = _sigma_urban(stability, x) if self.urban else _sigma_rural(stability, x)
        return max(sy, 0.1), max(sz, 0.1)


@dataclass(frozen=True)
class Conditions:
    """The weather the release happens in."""

    wind_from_degrees: float
    wind_speed_mps: float
    temperature_c: float
    relative_humidity: float = 0.7
    stability_class: str = "D"
    pressure_kpa: float = 101.3

    @property
    def downwind_bearing(self) -> float:
        """Bearing the plume travels along, radians clockwise from north."""
        return math.radians((self.wind_from_degrees + 180.0) % 360.0)

    def speed_at(self, height_m: float) -> float:
        """Wind speed at a height, from the 10 m value by the power law. Calm air still drifts."""
        reference = max(self.wind_speed_mps, 0.5)
        if height_m <= 10.0:
            return reference
        exponent = _WIND_EXPONENT.get(self.stability_class, 0.25)
        return reference * (height_m / 10.0) ** exponent


@dataclass(frozen=True)
class Release:
    """What is coming out, how fast, and in what state."""

    substance: Substance
    rate_kg_per_second: float
    duration_minutes: float

    quantity_kg: float | None = None
    """Total inventory; it caps the duration when the store empties sooner than expected."""

    temperature_c: float | None = None
    """None: the material leaves at ambient temperature."""

    pressure_kpa: float | None = None
    height_meters: float = 0.0
    diameter_meters: float = 0.5
    elapsed_minutes: float | None = None
    """How long the release has been running; None means "long enough to be at steady state"."""

    @property
    def effective_duration_minutes(self) -> float:
        """How long it can run for: its stated duration, or until the inventory runs out."""
        if self.quantity_kg is None or self.rate_kg_per_second <= 0:
            return self.duration_minutes
        from_inventory = self.quantity_kg / self.rate_kg_per_second / 60.0
        return min(self.duration_minutes, from_inventory)


@dataclass
class Zone:
    """One concentration band of the plume."""

    level: str
    threshold_mg_per_m3: float
    boundary: list[tuple[float, float]] = field(default_factory=list)
    """(latitude, longitude) pairs, closed by the caller."""

    area_square_meters: float = 0.0
    max_distance_meters: float = 0.0
    arrival_seconds: float | None = None
    dose_rate_msv_per_hour: float | None = None


@dataclass
class Prediction:
    """Everything the simulator gets back from one run of the model."""

    model: str
    substance: Substance
    zones: list[Zone]
    front_meters: float
    rear_meters: float
    effective_height_meters: float
    plume_rise_meters: float
    dense_gas: bool
    stability_class: str
    urban: bool


class GaussianPlume:
    """A Gaussian plume for one release in one set of conditions."""

    def __init__(
        self,
        origin: tuple[float, float],
        release: Release,
        conditions: Conditions,
        terrain: Terrain | None = None,
    ) -> None:
        """`origin` is the release point as (latitude, longitude)."""
        self.origin = origin
        self.release = release
        self.conditions = conditions
        self.terrain = terrain or Terrain()
        self.substance = release.substance

        ambient_pressure = conditions.pressure_kpa
        release_temperature = release.temperature_c if release.temperature_c is not None else conditions.temperature_c
        release_pressure = release.pressure_kpa if release.pressure_kpa is not None else ambient_pressure

        self._air_density = substances.air_density(conditions.temperature_c, ambient_pressure)
        self._gas_density = substances.gas_density(self.substance.molar_mass, release_temperature, release_pressure)
        self._density_ratio = self._gas_density / self._air_density

        # Heavier than air: it slumps instead of rising, and spreads sideways along the ground.
        self.dense_gas = self._density_ratio > 1.05 and not self.substance.buoyant
        self._spread_factor = min(2.5, 1 + 0.6 * (self._density_ratio - 1)) if self.dense_gas else 1.0
        self._depth_factor = 1 / self._spread_factor if self.dense_gas else 1.0

        self.plume_rise_meters = 0.0 if self.dense_gas else self._briggs_rise()
        self.effective_height_meters = 0.0 if self.dense_gas else release.height_meters + self.plume_rise_meters
        # The wind that carries the plume is the wind at the height it travels at.
        self.transport_speed = conditions.speed_at(max(self.effective_height_meters, 10.0))

    # ---- Plume rise ----

    def _buoyancy_flux(self) -> float:
        """Briggs buoyancy flux F, m4/s3. Positive when the release is lighter than the air around it."""
        if self.release.rate_kg_per_second <= 0 or self._gas_density <= 0:
            return 0.0
        volume_flow = self.release.rate_kg_per_second / self._gas_density
        return GRAVITY * volume_flow * (self._air_density - self._gas_density) / self._air_density

    def _briggs_rise(self) -> float:
        """How far a buoyant release lifts before it levels off."""
        flux = self._buoyancy_flux()
        if flux <= 0:
            return 0.0
        wind = max(self.conditions.speed_at(max(self.release.height_meters, 10.0)), 0.5)
        if self.conditions.stability_class in ("E", "F"):
            # Stable air puts a lid on the rise.
            gradient = 0.02 if self.conditions.stability_class == "E" else 0.035
            stability = GRAVITY / (self.conditions.temperature_c + 273.15) * gradient
            return 2.6 * (flux / (wind * stability)) ** (1 / 3)
        # Neutral or unstable: rise continues to the Briggs final-rise distance.
        final_distance = 49 * flux ** (5 / 8) if flux < 55 else 119 * flux ** (2 / 5)
        return min(1.6 * flux ** (1 / 3) * final_distance ** (2 / 3) / wind, 500.0)

    # ---- Travel ----

    @property
    def front_meters(self) -> float:
        """How far the leading edge has travelled. A release at steady state has reached its limit."""
        if self.release.elapsed_minutes is None:
            return MAX_RANGE_METERS
        return min(MAX_RANGE_METERS, max(0.0, self.release.elapsed_minutes) * 60.0 * self.transport_speed)

    @property
    def rear_meters(self) -> float:
        """
        How far the back of the cloud has moved. Zero while the release continues; once it stops, the
        cloud detaches from the source and travels away as a band.
        """
        elapsed = self.release.elapsed_minutes
        if elapsed is None:
            return 0.0
        over = elapsed - self.release.effective_duration_minutes
        return min(self.front_meters, max(0.0, over) * 60.0 * self.transport_speed)

    def travel_seconds(self, distance_m: float) -> float:
        return distance_m / max(self.transport_speed, 0.1)

    # ---- Concentration ----

    def _sigmas(self, x: float) -> tuple[float, float]:
        sy, sz = self.terrain.sigmas(self.conditions.stability_class, x)
        return sy * self._spread_factor, sz * self._depth_factor

    def _source_strength(self, x: float, depletion: float) -> float:
        """Release rate in mg/s at distance x, after deposition and decay have taken their share."""
        rate = self.release.rate_kg_per_second * 1e6
        if self.substance.half_life_minutes:
            minutes = self.travel_seconds(x) / 60.0
            rate *= 0.5 ** (minutes / self.substance.half_life_minutes)
        return rate * depletion

    def concentration(self, x: float, y: float, depletion: float = 1.0) -> float:
        """
        Ground-level concentration (mg/m3) x metres downwind and y metres across the plume, for a
        continuous release with reflection off the ground.
        """
        if self.release.rate_kg_per_second <= 0 or x < 1.0:
            return 0.0
        sy, sz = self._sigmas(x)
        height = self.effective_height_meters
        source = self._source_strength(x, depletion)
        lateral = math.exp(-(y * y) / (2 * sy * sy))
        vertical = math.exp(-(height * height) / (2 * sz * sz)) if height > 0 else 1.0
        return source / (math.pi * self.transport_speed * sy * sz) * lateral * vertical

    def centreline(self, x: float, depletion: float = 1.0) -> float:
        return self.concentration(x, 0.0, depletion)

    def _depletion_step(self, x: float, step: float, sz: float) -> float:
        """
        The fraction of the cloud left after settling over one step downwind (the source-depletion
        approximation): material lost to the ground is taken off the source strength.
        """
        velocity = self.substance.deposition_velocity
        if velocity <= 0:
            return 1.0
        height = self.effective_height_meters
        at_ground = math.exp(-(height * height) / (2 * sz * sz)) if height > 0 else 1.0
        loss = velocity * 2 / (math.sqrt(2 * math.pi) * sz * self.transport_speed) * at_ground * step
        return math.exp(-max(loss, 0.0))

    # ---- Zones ----

    def predict(self) -> Prediction:
        """The low, moderate and high zones, as polygons on the ground."""
        front, rear = self.front_meters, self.rear_meters
        profile = self._profile(front)
        zones = [self._zone(level, profile, rear) for level in LEVELS]
        return Prediction(
            model=MODEL_NAME,
            substance=self.substance,
            zones=[z for z in zones if z is not None],  # type: ignore[misc]
            front_meters=front,
            rear_meters=rear,
            effective_height_meters=self.effective_height_meters,
            plume_rise_meters=self.plume_rise_meters,
            dense_gas=self.dense_gas,
            stability_class=self.conditions.stability_class,
            urban=self.terrain.urban,
        )

    def _profile(self, front: float) -> list[tuple[float, float, float]]:
        """
        Centreline concentration along the plume: (distance, concentration, sigma_y), marching downwind
        so deposition and decay accumulate as they would along the path.
        """
        if front < 2.0 or self.release.rate_kg_per_second <= 0:
            return []
        points: list[tuple[float, float, float]] = []
        depletion = 1.0
        x, previous = 2.0, 2.0
        while x <= front:
            sy, sz = self._sigmas(x)
            depletion *= self._depletion_step(x, x - previous, sz)
            points.append((x, self.centreline(x, depletion), sy))
            previous, x = x, x * 1.06  # geometric steps: fine near the source, coarse far out
        return points

    def _zone(self, level: str, profile: list[tuple[float, float, float]], rear: float) -> Zone | None:
        """One concentration band, as a polygon around the stretch of plume that exceeds its threshold."""
        threshold = getattr(self.substance, level)
        zone = Zone(
            level=level,
            threshold_mg_per_m3=threshold,
            dose_rate_msv_per_hour=self.substance.dose_rate(threshold),
        )

        over = [(x, c, sy) for x, c, sy in profile if c >= threshold and x >= rear]
        if len(over) < 2:
            return None

        near, far = over[0][0], over[-1][0]
        zone.max_distance_meters = far
        zone.arrival_seconds = self.travel_seconds(near)

        # Walk out along one edge and back along the other, so the ring closes on itself.
        left: list[tuple[float, float]] = []
        right: list[tuple[float, float]] = []
        for index in range(_SAMPLES + 1):
            x = near + (far - near) * index / _SAMPLES
            centre, sigma_y = self._at(profile, x)
            half_width = sigma_y * math.sqrt(2 * math.log(centre / threshold)) if centre > threshold else 0.0
            left.append(self._ground(x, -half_width))
            right.append(self._ground(x, half_width))

        boundary = left + list(reversed(right))
        boundary.append(boundary[0])
        zone.boundary = boundary
        zone.area_square_meters = _area(boundary)
        return zone

    def _at(self, profile: list[tuple[float, float, float]], x: float) -> tuple[float, float]:
        """Centreline concentration and sigma_y at any distance, interpolated between profile points."""
        if not profile:
            return 0.0, 1.0
        if x <= profile[0][0]:
            return profile[0][1], profile[0][2]
        for (x0, c0, sy0), (x1, c1, sy1) in zip(profile, profile[1:]):
            if x <= x1:
                span = x1 - x0
                weight = 0.0 if span <= 0 else (x - x0) / span
                return c0 + (c1 - c0) * weight, sy0 + (sy1 - sy0) * weight
        return profile[-1][1], profile[-1][2]

    def _ground(self, downwind_m: float, crosswind_m: float) -> tuple[float, float]:
        """A point on the ground, from its position along and across the plume (flat-earth offsets)."""
        bearing = self.conditions.downwind_bearing
        sin, cos = math.sin(bearing), math.cos(bearing)
        north = downwind_m * cos - crosswind_m * sin
        east = downwind_m * sin + crosswind_m * cos
        origin_lat, origin_lon = self.origin
        latitude = origin_lat + north / _METERS_PER_DEGREE_LAT
        longitude = origin_lon + east / (_METERS_PER_DEGREE_LAT * math.cos(math.radians(origin_lat)))
        return latitude, longitude


def _area(boundary: list[tuple[float, float]]) -> float:
    """Area of a lat/long ring in square metres, by the shoelace formula on a local flat plane."""
    if len(boundary) < 4:
        return 0.0
    reference_lat = boundary[0][0]
    scale_lon = _METERS_PER_DEGREE_LAT * math.cos(math.radians(reference_lat))
    total = 0.0
    for (lat1, lon1), (lat2, lon2) in zip(boundary, boundary[1:]):
        x1, y1 = lon1 * scale_lon, lat1 * _METERS_PER_DEGREE_LAT
        x2, y2 = lon2 * scale_lon, lat2 * _METERS_PER_DEGREE_LAT
        total += x1 * y2 - x2 * y1
    return abs(total) / 2
