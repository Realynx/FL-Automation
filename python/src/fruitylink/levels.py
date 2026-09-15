"""Volume scales and the dB model of FL Studio's fader law (channel volume, mixer volume, sends).

FL's volume controls are raw integers on a curve that is neither linear in gain nor in dB. The
SDK models the law as ONE power curve over the whole scale - dB = 20 * FADER_EXPONENT *
log10(position / 0.8) - anchored at the fader position 0.8 that FL labels 0 dB (every mixer
insert's default 12800 of 16000; the channel-volume 10240 of 12800; the default send level).

The exponent 2.09 is the least-squares fit of every rendered measurement available, not a
guess from FL's fader hint (rendered calibration, Parking Lot Moon 2026-09-14: a held chord on
one channel through one insert, bars 110-111, RMS of the isolated render; plus the Ember Tides
full-mix master move measured as integrated loudness):

    move                          positions        measured   model    residual
    channel volume 12800 -> 6400  1.0   -> 0.5      -12.54    -12.58     -0.04
    mixer volume   12800 -> 6400  0.8   -> 0.4      -12.70    -12.58     +0.12
    mixer volume   12800 -> 5769  0.8   -> 0.3606   -14.21    -14.47     -0.26
    master volume  6800 -> 12800  0.425 -> 0.8      +11.80    +11.48     -0.32

One exponent therefore fits every point within 0.33 dB (the solved per-point exponents are
2.083 / 2.109 / 2.053 / 2.148), so the SDK keeps a single power law: no piecewise segment or
lookup table is needed, and channel volume, mixer volume and sends share the same curve, as FL's
own wire format does (a send level is the native 0..16000 fader value divided by 16000).

Two cautions. FL's fader hint claims +5.6 dB at the top (position 1.0), which is what the
superseded exponent 2.889 was derived from; this curve says +4.05 dB there, and the renders are
what the SDK promises, so ``FADER_MAX_DB`` follows the measured curve and ``FL_HINT_TOP_DB``
keeps FL's number for reference. And every measurement lies between position 0.36 and 0.8:
below about -20 dB the curve is an extrapolation (+-3 dB), so confirm large cuts with a render.
Every helper takes ``exponent=`` so a fresh calibration can override the default.

Scales (raw integers):

- Mixer track volume: 0..16000, 12800 = 0 dB (0.8), 16000 = +4.1 dB (FL's hint says +5.6).
- Channel volume: 0..12800 (FL's default 10000 = 78 %), 10240 = 0 dB (0.8), 12800 = +4.1 dB.
- Send level (``set_mixer_send``): float 0..1 = native 0..16000; 0.8 = unity (0 dB).
"""

import math

FADER_UNITY = 0.8
"""Normalized fader position FL labels 0 dB (mixer 12800/16000; the default send level)."""

FADER_EXPONENT = 2.09
"""Power-curve exponent measured by render calibration (least-squares fit, residuals <= 0.33 dB).

Superseded the 2.889 that FL's "+5.6 dB at the top" hint implies: that model predicted a 17.4 dB
drop for halving a fader, where the renders measure 12.5-12.7 dB. See the module docstring."""

FL_HINT_TOP_DB = 5.6
"""Gain at the fader top as FL's own hint displays it; the rendered audio says FADER_MAX_DB."""

FADER_MAX_DB = 20 * FADER_EXPONENT * math.log10(1 / FADER_UNITY)
"""Gain at the fader top (normalized 1.0) on the measured curve: about +4.05 dB."""

MIXER_VOLUME_MAX = 16000
MIXER_VOLUME_UNITY = 12800
CHANNEL_VOLUME_MAX = 12800
CHANNEL_VOLUME_UNITY = 10240
CHANNEL_VOLUME_DEFAULT = 10000
SEND_UNITY = 0.8
"""``MixerTrack.send_to`` level that is 0 dB; the level every untouched insert's Master route reads."""


def _check_exponent(exponent: float) -> float:
    if isinstance(exponent, bool) or not isinstance(exponent, (int, float)) or not 0 < exponent < 10:
        raise ValueError("Fader exponent must be a number within (0, 10).")
    return float(exponent)


def fader_db(position: float, *, exponent: float = FADER_EXPONENT) -> float:
    """Gain in dB of a normalized fader position 0..1 (0 -> -inf, 0.8 -> 0.0, 1.0 -> +4.05)."""
    exponent = _check_exponent(exponent)
    if isinstance(position, bool) or not isinstance(position, (int, float)) or not 0 <= position <= 1:
        raise ValueError("Fader position must be a number within 0..1.")
    if position == 0:
        return -math.inf
    return 20 * exponent * math.log10(position / FADER_UNITY)


def fader_position(db: float, *, exponent: float = FADER_EXPONENT) -> float:
    """Normalized fader position 0..1 for a gain in dB (-inf -> 0). Refuses gains above the fader top."""
    exponent = _check_exponent(exponent)
    if isinstance(db, bool) or not isinstance(db, (int, float)) or math.isnan(db):
        raise ValueError("Gain must be a number of decibels (or -inf).")
    if db == -math.inf:
        return 0.0
    top = fader_db(1.0, exponent=exponent)
    if db > top + 1e-9:
        raise ValueError(f"{db:+.2f} dB is above the fader top ({top:+.1f} dB); trim with a plugin gain instead.")
    return min(1.0, FADER_UNITY * 10 ** (db / (20 * exponent)))


def _from_db(db: float, maximum: int, exponent: float) -> int:
    return int(round(fader_position(db, exponent=exponent) * maximum))


def _to_db(raw: int, maximum: int, exponent: float, what: str) -> float:
    if isinstance(raw, bool) or not isinstance(raw, int) or not 0 <= raw <= maximum:
        raise ValueError(f"{what} must be an integer within 0..{maximum}.")
    return fader_db(raw / maximum, exponent=exponent)


def mixer_volume_to_db(raw: int, *, exponent: float = FADER_EXPONENT) -> float:
    """dB of a raw mixer volume 0..16000 (12800 -> 0.0; 16000 -> +4.05; 6400 -> about -12.6, measured)."""
    return _to_db(raw, MIXER_VOLUME_MAX, exponent, "Mixer volume")


def mixer_volume_from_db(db: float, *, exponent: float = FADER_EXPONENT) -> int:
    """Raw mixer volume 0..16000 for a gain in dB (0.0 -> 12800; -6 -> 9197; above +4.05 refused)."""
    return _from_db(db, MIXER_VOLUME_MAX, exponent)


def channel_volume_to_db(raw: int, *, exponent: float = FADER_EXPONENT) -> float:
    """dB of a raw channel volume 0..12800 (10240 -> 0.0; 10000 -> about -0.4; 5000 -> about -13)."""
    return _to_db(raw, CHANNEL_VOLUME_MAX, exponent, "Channel volume")


def channel_volume_from_db(db: float, *, exponent: float = FADER_EXPONENT) -> int:
    """Raw channel volume 0..12800 for a gain in dB (0.0 -> 10240; -6 -> 7358; above +4.05 refused)."""
    return _from_db(db, CHANNEL_VOLUME_MAX, exponent)


def send_level_to_db(level: float, *, exponent: float = FADER_EXPONENT) -> float:
    """dB of a ``set_mixer_send`` level 0..1 (0.8 -> 0.0; 1.0 -> +4.05).

    Sends have no calibration of their own: the native send value is ``level * 16000`` on the
    mixer fader scale, so they follow the mixer curve above.
    """
    if isinstance(level, bool) or not isinstance(level, (int, float)) or not 0 <= level <= 1:
        raise ValueError("Send level must be a number within 0..1.")
    return fader_db(float(level), exponent=exponent)


def send_level_from_db(db: float, *, exponent: float = FADER_EXPONENT) -> float:
    """``set_mixer_send`` level 0..1 for a gain in dB (0.0 -> 0.8; above +4.05 refused)."""
    return fader_position(db, exponent=exponent)


def volume_table(step: int = 800, *, exponent: float = FADER_EXPONENT) -> tuple[tuple[int, float, float], ...]:
    """(raw, channel dB, mixer dB) rows every ``step`` raw units up to 12800, for docs and sanity checks.

    Raw values above 12800 exist only on the mixer scale (up to 16000); they are not listed.
    """
    if isinstance(step, bool) or not isinstance(step, int) or step <= 0:
        raise ValueError("Table step must be a positive integer.")
    rows = []
    for raw in range(0, CHANNEL_VOLUME_MAX + 1, step):
        rows.append((raw, channel_volume_to_db(raw, exponent=exponent), mixer_volume_to_db(raw, exponent=exponent)))
    return tuple(rows)
