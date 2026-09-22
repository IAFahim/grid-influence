import argparse
import ctypes
import fcntl
import json
import os
import platform
import shutil
import struct
import subprocess
from pathlib import Path


PERF_EVENT_OPEN_X64 = 298
PERF_TYPE_RAW = 4
PERF_FORMAT_GROUP = 1 << 3
PERF_FORMAT_TOTAL_TIME_ENABLED = 1
PERF_FORMAT_TOTAL_TIME_RUNNING = 1 << 1
PERF_EVENT_DISABLED = 1
PERF_EVENT_EXCLUDE_KERNEL = 1 << 5
PERF_EVENT_EXCLUDE_HYPERVISOR = 1 << 6
PERF_EVENT_IOC_ENABLE = 0x2400
PERF_EVENT_IOC_DISABLE = 0x2401
PERF_EVENT_IOC_RESET = 0x2403
EVENTS = {
    "cycles": 0x3C,
    "instructions": 0xC0,
    "branches": 0xC4,
    "branch_misses": 0xC5,
}
SCENARIOS = (
    "field-tick",
    "field-tick-wide",
    "naive-tick",
    "naive-tick-wide",
    "query",
    "ppm-decode",
    "ppm-encode",
    "world-apply",
    "world-apply-multi",
    "world-query",
)


def open_counter(process_id, config, group):
    attributes = bytearray(128)
    struct.pack_into("IIQ", attributes, 0, PERF_TYPE_RAW, len(attributes), config)
    struct.pack_into(
        "Q",
        attributes,
        32,
        PERF_FORMAT_GROUP | PERF_FORMAT_TOTAL_TIME_ENABLED | PERF_FORMAT_TOTAL_TIME_RUNNING,
    )
    struct.pack_into(
        "Q",
        attributes,
        40,
        PERF_EVENT_DISABLED | PERF_EVENT_EXCLUDE_KERNEL | PERF_EVENT_EXCLUDE_HYPERVISOR,
    )
    buffer = (ctypes.c_char * len(attributes)).from_buffer(attributes)
    descriptor = ctypes.CDLL(None, use_errno=True).syscall(
        PERF_EVENT_OPEN_X64,
        ctypes.byref(buffer),
        process_id,
        -1,
        group,
        0,
    )
    if descriptor >= 0:
        return descriptor
    error = ctypes.get_errno()
    raise OSError(error, os.strerror(error))


def measure(dotnet, assembly, cpu, scenario):
    process = subprocess.Popen(
        ["taskset", "--cpu-list", str(cpu), dotnet, str(assembly), "--pmu", scenario],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    descriptors = []
    try:
        ready = process.stdout.readline()
        if not ready.startswith("ready "):
            raise RuntimeError(ready + process.stderr.read())
        ticks = int(ready.split()[1])
        for config in EVENTS.values():
            descriptors.append(open_counter(process.pid, config, descriptors[0] if descriptors else -1))
        leader = descriptors[0]
        fcntl.ioctl(leader, PERF_EVENT_IOC_RESET, 1)
        fcntl.ioctl(leader, PERF_EVENT_IOC_ENABLE, 1)
        process.stdin.write("go\n")
        process.stdin.flush()
        receipt = process.stdout.readline().strip()
        fcntl.ioctl(leader, PERF_EVENT_IOC_DISABLE, 1)
        count, enabled, running, *counts = struct.unpack("7Q", os.read(leader, 56))
        process.stdin.write("stop\n")
        process.stdin.flush()
        process.wait(timeout=30)
        if process.returncode != 0 or count != len(EVENTS) or enabled == 0 or running == 0:
            raise RuntimeError(process.stderr.read())
        scale = enabled / running / ticks
        return {
            "scenario": scenario,
            "ticks": ticks,
            "running_fraction": running / enabled,
            "per_tick": {name: value * scale for name, value in zip(EVENTS, counts)},
            "receipt": receipt,
        }
    finally:
        for descriptor in descriptors:
            os.close(descriptor)
        if process.poll() is None:
            process.kill()
            process.wait()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--assembly",
        type=Path,
        default=Path(__file__).parent / "bin/Release/net10.0/GridInfluenceBenchmarks.dll",
    )
    parser.add_argument("--cpu", type=int, default=0)
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--scenario", action="append", choices=SCENARIOS)
    arguments = parser.parse_args()
    if platform.machine() != "x86_64" or arguments.dotnet is None or not arguments.assembly.is_file():
        raise SystemExit(2)
    records = []
    failures = 0
    for scenario in arguments.scenario or SCENARIOS:
        try:
            record = measure(arguments.dotnet, arguments.assembly, arguments.cpu, scenario)
        except Exception as error:
            record = {
                "scenario": scenario,
                "error_type": type(error).__name__,
                "error": str(error),
            }
            failures += 1
        records.append(record)
        print(json.dumps(record))
    if arguments.output:
        arguments.output.write_text(
            "".join(json.dumps(record, sort_keys=True) + "\n" for record in records),
            encoding="utf-8",
        )
    if failures:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
