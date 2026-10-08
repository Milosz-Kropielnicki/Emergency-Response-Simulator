"""Placeholder dispersion model.

Draws three nested ellipses stretched downwind of the release, scaled crudely by release rate,
wind speed and atmospheric stability. It exists so the C# side has a real service to integrate with;
it is NOT a physical model. Phase 12 replaces it with a Gaussian plume / dispersion model.
"""

import math

from .models import GeoPoint, PlumeRequest, PlumeZone

MODEL_NAME = "stub-ellipse-v0"

# Stable air keeps a plume narrow and long; unstable air spreads it wide and short.
_STABILITY_SPREAD = {"A": 0.55, "B": 0.45, "C": 0.38, "D": 0.30, "E": 0.24, "F": 0.18}

_LEVELS = (("high", 0.25), ("moderate", 0.55), ("low", 1.0))

_METERS_PER_DEGREE_LAT = 111_320.0


def predict(request: PlumeRequest, points_per_ring: int = 48) -> list[PlumeZone]:
    weather = request.weather
    downwind_bearing = math.radians((weather.wind_from_degrees + 180.0) % 360.0)

    # Longer reach with a bigger release; a stronger wind carries it further but dilutes it faster.
    wind = max(weather.wind_speed_mps, 0.5)
    reach_m = 400.0 * math.sqrt(request.release_rate_kg_per_second) * (1.0 + 0.15 * wind)
    reach_m *= min(1.0, 0.4 + request.duration_minutes / 60.0)
    width_ratio = _STABILITY_SPREAD[weather.stability_class]

    origin = request.release_location
    zones = []
    for level, fraction in _LEVELS:
        length = reach_m * fraction
        half_width = length * width_ratio
        ring = []
        for i in range(points_per_ring):
            theta = 2 * math.pi * i / points_per_ring
            # Ellipse starting slightly upwind of the source and extending downwind.
            along = length / 2 * (1 + math.cos(theta)) - 0.05 * length
            across = half_width * math.sin(theta)
            ring.append(_offset(origin, along, across, downwind_bearing))
        zones.append(PlumeZone(level=level, boundary=ring))
    return zones


def _offset(origin: GeoPoint, along_m: float, across_m: float, bearing: float) -> GeoPoint:
    """Moves `along_m` metres along `bearing` and `across_m` metres to its right (flat-earth approximation)."""
    north = along_m * math.cos(bearing) - across_m * math.sin(bearing)
    east = along_m * math.sin(bearing) + across_m * math.cos(bearing)
    lat = origin.latitude + north / _METERS_PER_DEGREE_LAT
    lon = origin.longitude + east / (_METERS_PER_DEGREE_LAT * math.cos(math.radians(origin.latitude)))
    return GeoPoint(latitude=lat, longitude=lon)
