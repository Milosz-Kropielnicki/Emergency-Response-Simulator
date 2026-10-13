"""What is being released, and when it starts to hurt people.

Each substance carries the three concentration thresholds the simulator draws zones from, plus the
physical properties the dispersion model needs: molar mass (for gas density), how fast it settles out
of the air, and how quickly it decays.

Thresholds are 60-minute exposure values in mg/m3, in the spirit of the US AEGLs:

    low      ~ AEGL-1: noticeable discomfort, no lasting harm
    moderate ~ AEGL-2: serious or lasting effects, escape impaired
    high     ~ AEGL-3: life-threatening

They are teaching values, rounded and simplified, not a basis for real-world decisions. The C# side
keeps a matching short list in Simulation/Hazards/PlumeModel.cs for when this service is unavailable.

Radiological material is the exception: its hazard is dose, not concentration, so its thresholds are
given as dose rates (mSv/h) and converted with `dose_rate_per_mg_m3`. That conversion stands in for a
proper dose model and only holds for the submersion (cloud) pathway.
"""

from dataclasses import dataclass
from typing import Literal

MaterialClass = Literal["chemical", "smoke", "toxic-gas", "biological", "radiological"]

# Molar mass of air, kg/kmol, and the universal gas constant, kJ/(kmol K).
AIR_MOLAR_MASS = 28.96
GAS_CONSTANT = 8.314


@dataclass(frozen=True)
class Substance:
    """One releasable material and the thresholds its zones are drawn at."""

    name: str
    material_class: MaterialClass

    # Concentration thresholds, mg/m3, increasing in severity.
    low: float
    moderate: float
    high: float

    molar_mass: float = AIR_MOLAR_MASS
    """kg/kmol. Heavier-than-air gases slump and hug the ground."""

    deposition_velocity: float = 0.0
    """m/s at which the material settles onto the ground, depleting the cloud as it travels."""

    half_life_minutes: float | None = None
    """Radioactive or biological decay; None for a material that does not decay."""

    buoyant: bool = False
    """Released hot and rising (smoke), rather than drifting at the height it left the source."""

    dose_rate_per_mg_m3: float | None = None
    """mSv/h per mg/m3 in the cloud, for radiological material (submersion pathway only)."""

    advisory: str = ""
    """What command should be told to do about it, shown with the prediction."""

    @property
    def is_radiological(self) -> bool:
        return self.material_class == "radiological"

    def dose_rate(self, concentration_mg_m3: float) -> float | None:
        """Dose rate (mSv/h) in a cloud at this concentration, for radiological material."""
        if self.dose_rate_per_mg_m3 is None:
            return None
        return concentration_mg_m3 * self.dose_rate_per_mg_m3

    def thresholds(self) -> dict[str, float]:
        return {"low": self.low, "moderate": self.moderate, "high": self.high}


# 1 ppm chlorine ~ 2.9 mg/m3; ammonia ~ 0.7; sulphur dioxide ~ 2.6; hydrogen sulphide ~ 1.4.
CHLORINE = Substance(
    name="chlorine",
    material_class="chemical",
    low=1.5,
    moderate=5.8,
    high=58.0,
    molar_mass=70.9,
    deposition_velocity=0.005,
    advisory="Heavier than air: it collects in basements, docks and low ground. Shelter in place above ground level.",
)

AMMONIA = Substance(
    name="ammonia",
    material_class="chemical",
    low=21.0,
    moderate=112.0,
    high=770.0,
    molar_mass=17.0,
    deposition_velocity=0.01,
    advisory="Lighter than air and water-soluble: it lifts and washes out, but burns eyes and airways near the source.",
)

SULPHUR_DIOXIDE = Substance(
    name="sulphur dioxide",
    material_class="chemical",
    low=0.5,
    moderate=2.0,
    high=80.0,
    molar_mass=64.1,
    deposition_velocity=0.008,
    advisory="Heavier than air. Asthmatics are affected well below the general threshold.",
)

HYDROGEN_SULPHIDE = Substance(
    name="hydrogen sulphide",
    material_class="chemical",
    low=0.7,
    moderate=23.0,
    high=70.0,
    molar_mass=34.1,
    deposition_velocity=0.005,
    advisory="Deadens the sense of smell at dangerous concentrations: crews cannot rely on the smell as a warning.",
)

CHEMICAL_GENERIC = Substance(
    name="industrial chemical",
    material_class="chemical",
    low=1.5,
    moderate=5.8,
    high=58.0,
    molar_mass=70.9,
    deposition_velocity=0.005,
    advisory="Treat as an unknown irritant gas until identified: chlorine-like thresholds assumed.",
)

TOXIC_GAS_GENERIC = Substance(
    name="toxic gas",
    material_class="toxic-gas",
    low=1.5,
    moderate=5.8,
    high=58.0,
    molar_mass=70.9,
    deposition_velocity=0.005,
    advisory="Unidentified toxic gas: assume the worst until a detector or a manifest says otherwise.",
)

# Combustion products, as fine particulate (PM2.5-like). Thresholds are short-exposure smoke values.
SMOKE = Substance(
    name="smoke",
    material_class="smoke",
    low=0.5,
    moderate=5.0,
    high=50.0,
    molar_mass=29.0,
    deposition_velocity=0.01,
    buoyant=True,
    advisory="Buoyant: it lifts over the near field and comes back down further out, so the worst ground level is not at the fire.",
)

# A spore-forming aerosol, the usual training stand-in. Thresholds are rough infectious-dose bands,
# not an exposure standard: biological material harms by dose inhaled over time, not peak concentration.
BIOLOGICAL_AEROSOL = Substance(
    name="biological aerosol",
    material_class="biological",
    low=1e-5,
    moderate=1e-4,
    high=1e-3,
    molar_mass=29.0,
    deposition_velocity=0.002,
    half_life_minutes=120.0,
    advisory="Odourless and invisible: only detection confirms it. Respiratory protection and decontamination of anyone exposed.",
)

# Caesium-137-like particulate. Thresholds below are dose rates in mSv/h, converted on construction.
_RADIOLOGICAL_DOSE_RATE_PER_MG_M3 = 2.0
RADIOLOGICAL = Substance(
    name="radioactive material",
    material_class="radiological",
    low=0.1 / _RADIOLOGICAL_DOSE_RATE_PER_MG_M3,
    moderate=1.0 / _RADIOLOGICAL_DOSE_RATE_PER_MG_M3,
    high=10.0 / _RADIOLOGICAL_DOSE_RATE_PER_MG_M3,
    molar_mass=29.0,
    deposition_velocity=0.001,
    dose_rate_per_mg_m3=_RADIOLOGICAL_DOSE_RATE_PER_MG_M3,
    advisory="Dose matters more than concentration: limit time in the zones, rotate crews, and survey everyone leaving.",
)

ALL: tuple[Substance, ...] = (
    CHLORINE,
    AMMONIA,
    SULPHUR_DIOXIDE,
    HYDROGEN_SULPHIDE,
    CHEMICAL_GENERIC,
    TOXIC_GAS_GENERIC,
    SMOKE,
    BIOLOGICAL_AEROSOL,
    RADIOLOGICAL,
)

# Words that identify a substance, longest first so "hydrogen sulphide" wins over "hydrogen".
_KEYWORDS: tuple[tuple[str, Substance], ...] = (
    ("hydrogen sulphide", HYDROGEN_SULPHIDE),
    ("hydrogen sulfide", HYDROGEN_SULPHIDE),
    ("sulphur dioxide", SULPHUR_DIOXIDE),
    ("sulfur dioxide", SULPHUR_DIOXIDE),
    ("biological", BIOLOGICAL_AEROSOL),
    ("radiological", RADIOLOGICAL),
    ("radioactive", RADIOLOGICAL),
    ("anthrax", BIOLOGICAL_AEROSOL),
    ("chlorine", CHLORINE),
    ("ammonia", AMMONIA),
    ("caesium", RADIOLOGICAL),
    ("cesium", RADIOLOGICAL),
    ("cobalt", RADIOLOGICAL),
    ("iodine-131", RADIOLOGICAL),
    ("spore", BIOLOGICAL_AEROSOL),
    ("aerosol", BIOLOGICAL_AEROSOL),
    ("smoke", SMOKE),
    ("soot", SMOKE),
    ("so2", SULPHUR_DIOXIDE),
    ("h2s", HYDROGEN_SULPHIDE),
    ("nh3", AMMONIA),
    ("cl2", CHLORINE),
    ("toxic", TOXIC_GAS_GENERIC),
    ("chemical", CHEMICAL_GENERIC),
)


def find(name: str | None) -> Substance:
    """The substance a name refers to; an unidentified one is treated as a toxic gas."""
    key = (name or "").strip().lower()
    if not key:
        return TOXIC_GAS_GENERIC
    for keyword, substance in _KEYWORDS:
        if keyword in key:
            # Keep the name command actually used, so the COP and the review say what was reported.
            return substance if substance.name == key else _renamed(substance, name or substance.name)
    return _renamed(TOXIC_GAS_GENERIC, name or TOXIC_GAS_GENERIC.name)


def _renamed(substance: Substance, name: str) -> Substance:
    from dataclasses import replace

    return replace(substance, name=name.strip())


def air_density(temperature_c: float, pressure_kpa: float) -> float:
    """Ambient air density, kg/m3, from the ideal gas law."""
    return pressure_kpa * AIR_MOLAR_MASS / (GAS_CONSTANT * (temperature_c + 273.15))


def gas_density(molar_mass: float, temperature_c: float, pressure_kpa: float) -> float:
    """Released material's density, kg/m3, at its own temperature and pressure."""
    return pressure_kpa * molar_mass / (GAS_CONSTANT * (temperature_c + 273.15))
