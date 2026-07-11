#!/usr/bin/env python3
"""Create a silent, mono PCM WAV smoke track without replacing user audio."""

from __future__ import annotations

import argparse
import os
import sys
import wave
from decimal import Decimal, DecimalException, InvalidOperation, ROUND_HALF_UP
from pathlib import Path
from typing import Sequence


_CHANNELS = 1
_SAMPLE_WIDTH_BYTES = 2
_CHUNK_FRAMES = 65_536
# A RIFF/WAV header stores its size in an unsigned 32-bit field.  The RIFF
# size is the 36-byte header after its own size field plus the PCM data.
_MAX_FRAMES = (0xFFFFFFFF - 36) // _SAMPLE_WIDTH_BYTES


class _CreationError(RuntimeError):
    """An error that is safe to report from this command-line utility."""


def _positive_duration(value: str) -> Decimal:
    try:
        duration = Decimal(value)
    except InvalidOperation as error:
        raise argparse.ArgumentTypeError("must be a finite positive number") from error

    if not duration.is_finite() or duration <= 0:
        raise argparse.ArgumentTypeError("must be a finite positive number")
    return duration


def _sample_rate(value: str) -> int:
    try:
        sample_rate = int(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("must be a positive integer") from error

    if not 0 < sample_rate <= 0xFFFFFFFF:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return sample_rate


def _frame_count(duration_seconds: Decimal, sample_rate: int) -> int:
    try:
        frames_decimal = duration_seconds * Decimal(sample_rate)
        frames = int(frames_decimal.to_integral_value(rounding=ROUND_HALF_UP))
    except (DecimalException, OverflowError, ValueError) as error:
        raise _CreationError("duration-seconds is out of range") from error

    if not 0 < frames <= _MAX_FRAMES:
        raise _CreationError("duration-seconds is out of range")
    return frames


def _remove_created_file(path: Path, created_stat: os.stat_result) -> None:
    """Remove only the file this invocation created after a write failure."""

    try:
        current_stat = path.stat(follow_symlinks=False)
    except FileNotFoundError:
        return

    if (current_stat.st_dev, current_stat.st_ino) == (
        created_stat.st_dev,
        created_stat.st_ino,
    ):
        try:
            path.unlink()
        except FileNotFoundError:
            pass


def create_silence(path: Path, duration_seconds: Decimal, sample_rate: int) -> None:
    """Create a silent PCM WAV at *path*, failing if it already exists."""

    frames = _frame_count(duration_seconds, sample_rate)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL

    try:
        descriptor = os.open(path, flags, 0o644)
    except FileExistsError as error:
        raise _CreationError("target already exists; refusing to overwrite") from error
    except OSError as error:
        raise _CreationError("could not create target") from error

    created_stat = os.fstat(descriptor)
    try:
        with os.fdopen(descriptor, "wb") as output:
            descriptor = -1
            with wave.open(output, "wb") as wav_file:
                wav_file.setnchannels(_CHANNELS)
                wav_file.setsampwidth(_SAMPLE_WIDTH_BYTES)
                wav_file.setframerate(sample_rate)

                silence = b"\x00\x00" * min(_CHUNK_FRAMES, frames)
                remaining_frames = frames
                while remaining_frames:
                    chunk_frames = min(remaining_frames, _CHUNK_FRAMES)
                    wav_file.writeframesraw(silence[: chunk_frames * _SAMPLE_WIDTH_BYTES])
                    remaining_frames -= chunk_frames
    except BaseException:
        _remove_created_file(path, created_stat)
        raise
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Create an absent silent mono PCM WAV smoke track."
    )
    parser.add_argument("--path", required=True, type=Path)
    parser.add_argument(
        "--duration-seconds", required=True, type=_positive_duration, metavar="SECONDS"
    )
    parser.add_argument("--sample-rate", required=True, type=_sample_rate, metavar="HERTZ")
    return parser


def main(arguments: Sequence[str] | None = None) -> int:
    parsed = _parser().parse_args(arguments)
    try:
        create_silence(parsed.path, parsed.duration_seconds, parsed.sample_rate)
    except _CreationError as error:
        print(f"create-smoke-track: {error}", file=sys.stderr)
        return 1
    except OSError:
        print("create-smoke-track: could not write target", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
