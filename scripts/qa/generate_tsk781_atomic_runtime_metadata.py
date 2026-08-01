#!/usr/bin/env python3
"""Generate dark Atomic runtime metadata from the TSK-781 cutover manifest."""

from __future__ import annotations

import argparse
import csv
import hashlib
import re
import sys
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SOURCE_MANIFEST = ROOT / "output/qa/tsk781-atomic-cutover-manifest.csv"
REPLAY_CLASSIFICATION = ROOT / "output/qa/tsk781-replay-policy-classification.csv"
COMMAND_OUTPUT = ROOT / "RentalCommand.Data/Atomic/Runtime/Generated/AtomicRuntimeGeneratedCommandManifest.g.cs"
ADMISSION_OUTPUT = ROOT / "RentalCommand.Data/Atomic/Runtime/Generated/AtomicRuntimeGeneratedAdmissionDependencyManifest.g.cs"
SIMULATION_REGISTRY_OUTPUT = ROOT / "RentalCommand.Data/Atomic/Runtime/Generated/AtomicRuntimeGeneratedSimulationRegistry.g.cs"

EXPECTED_REGISTRATIONS = 202
EXPECTED_API_REGISTRATIONS = 183
EXPECTED_ENGINE_REGISTRATIONS = 19
HISTORICAL_ATTENTION_COMMANDS = {
    "RentalCommand.Core.Payments.RecoverHistoricalRentChargeCommand":
        "Fold tenant-account row lock, ledger insertion, and replay authorization into one Data-owned Runtime attempt; keep historical-rent-charge.recover.v1 receipt replay exact.",
    "RentalCommand.Core.Payments.RecoverRefundedTenantAllocationCommand":
        "Fold refunded allocation reconciliation into one Data-owned Runtime attempt; keep refunded-tenant-allocation.recover.v1 receipt replay exact.",
    "RentalCommand.Core.Payments.RecoverLateFeeChargesCommand":
        "Fold late-fee recovery into one Data-owned Runtime attempt; keep late-fee-charges.recover.v1 receipt replay exact.",
    "RentalCommand.Core.Payments.RecoverOpeningSecurityDepositsCommand":
        "Fold opening deposit recovery into one Data-owned Runtime attempt; keep opening-security-deposits.recover.v1 receipt replay exact.",
    "RentalCommand.Core.Leasing.ReconcileHistoricalPossessionCommand":
        "Fold historical possession reconciliation into one Data-owned Runtime attempt; keep lease-management.reconcile-historical-possession.v1 receipt replay exact.",
}
FORBIDDEN_GENERATED_TOKENS = (
    "AddAtomicCommandHandler",
    "IAtomicUnitOfWork",
    "IAtomicCommandHandler",
    "IAtomicReplayAuthorizer",
    "AtomicCommandDisposition.Joined",
    "ExecuteSql",
    "FromSql",
    "DbCommand",
    "CommandText",
    "set_config",
    "current_setting",
    "SET ROLE",
    "RESET ROLE",
    "IServiceProvider",
    "GetRequiredService",
    "HttpClient",
    "IFileStorage",
    "IPaymentProvider",
    "IAccountingProvider",
    "ILlmProvider",
    "ISmsProvider",
    "IPushSender",
    "INotificationChannel",
)
SIMULATION_COMMANDS = (
    "RentalCommand.Core.Time.SetSimulationClockCommand",
    "RentalCommand.Core.Time.AdvanceSimulationClockCommand",
    "RentalCommand.Core.Time.FreezeSimulationClockCommand",
    "RentalCommand.Core.Time.UnfreezeSimulationClockCommand",
    "RentalCommand.Core.Time.ResetSimulationClockCommand",
    "RentalCommand.Core.Time.EnqueueSimulationWorkerCommand",
)


@dataclass(frozen=True)
class Registration:
    host: str
    registration: str
    command: str
    result: str
    current_handler: str
    receipt_contract: str
    replay_policy: str
    final_replay_policy: str
    policy_evidence: str
    caller_count: int
    identity_literals: tuple[str, ...]
    historical_decision: str


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(newline="") as file:
        return list(csv.DictReader(file))


def manifest_sha() -> str:
    return hashlib.sha256(SOURCE_MANIFEST.read_bytes()).hexdigest()


def csharp(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def extract_identity_literals(rows: list[dict[str, str]]) -> dict[str, set[str]]:
    values: dict[str, set[str]] = {}
    for row in rows:
        if row["kind"] != "atomic_execute_call" or not row["receipt_contract"]:
            continue
        match = re.search(r"identity=([^.;]+)", row["notes"])
        if match is None:
            continue
        values.setdefault(row["receipt_contract"], set()).add(match.group(1))
    return values


def replay_rows() -> dict[tuple[str, str, str], dict[str, str]]:
    if not REPLAY_CLASSIFICATION.exists():
        return {}
    rows = read_csv(REPLAY_CLASSIFICATION)
    return {
        (row["registered_in"], row["command"], row["result"]): row
        for row in rows
    }


def load_registrations() -> tuple[list[Registration], str]:
    if not SOURCE_MANIFEST.exists():
        raise SystemExit(f"Missing source manifest: {SOURCE_MANIFEST.relative_to(ROOT)}")

    rows = read_csv(SOURCE_MANIFEST)
    registrations = [
        row for row in rows
        if row["category"] == "api_engine_registration"
    ]
    if len(registrations) != EXPECTED_REGISTRATIONS:
        raise SystemExit(
            f"Expected {EXPECTED_REGISTRATIONS} registrations, found {len(registrations)}")

    missing = [
        row for row in registrations
        if not row["symbol"]
        or not row["handler"]
        or not row["result"]
        or not row["registered_in"]
        or not row["receipt_contract"]
        or not row["replay_policy"]
        or not row["classification"]
    ]
    if missing:
        raise SystemExit(f"Unclassified or incomplete registrations: {len(missing)}")

    keys = [
        (row["registered_in"], row["symbol"], row["result"])
        for row in registrations
    ]
    if len(keys) != len(set(keys)):
        raise SystemExit("Duplicate host/command/result registrations found")

    host_counts = {
        host: sum(1 for row in registrations if row["registered_in"] == host)
        for host in ("api", "engine")
    }
    if host_counts != {
        "api": EXPECTED_API_REGISTRATIONS,
        "engine": EXPECTED_ENGINE_REGISTRATIONS,
    }:
        raise SystemExit(f"Unexpected registration host counts: {host_counts}")

    identity_literals = extract_identity_literals(rows)
    replay = replay_rows()
    registration_keys = {
        (row["registered_in"], row["symbol"], row["result"])
        for row in registrations
    }
    if registration_keys != set(replay):
        raise SystemExit("Replay classification registration set does not match the source manifest")

    output: list[Registration] = []
    for row in sorted(registrations, key=lambda item: (
        item["registered_in"],
        int(item["line"]),
        item["symbol"],
        item["result"],
    )):
        replay_key = (row["registered_in"], row["symbol"], row["result"])
        replay_row = replay[replay_key]
        expected_replay_fields = {
            "registration": f"{row['path']}:{row['line']}",
            "handler": row["handler"],
            "caller_count": row["caller_count"],
            "receipt_contract": row["receipt_contract"],
            "manifest_replay_policy": row["replay_policy"],
        }
        replay_mismatches = {
            key: (expected, replay_row.get(key, ""))
            for key, expected in expected_replay_fields.items()
            if replay_row.get(key, "") != expected
        }
        if replay_mismatches:
            raise SystemExit(
                f"Replay classification drift for {row['symbol']}: {replay_mismatches}")
        historical_decision = HISTORICAL_ATTENTION_COMMANDS.get(row["symbol"], "not_historical_attention")
        output.append(Registration(
            host=row["registered_in"],
            registration=f"{row['path']}:{row['line']}",
            command=row["symbol"],
            result=row["result"],
            current_handler=row["handler"],
            receipt_contract=row["receipt_contract"],
            replay_policy=row["replay_policy"],
            final_replay_policy=replay_row.get("final_policy", "manifest-policy-pending-domain-adapter"),
            policy_evidence=replay_row.get(
                "authorization_query_or_blocker",
                "Generated adapter must provide a handler-owned read-only database policy before receipt replay."),
            caller_count=int(row["caller_count"]),
            identity_literals=tuple(sorted(identity_literals.get(row["receipt_contract"], ()))),
            historical_decision=historical_decision,
        ))

    historical = {
        item.command for item in output
        if item.historical_decision != "not_historical_attention"
    }
    if historical != set(HISTORICAL_ATTENTION_COMMANDS):
        raise SystemExit(f"Historical attention decision mismatch: {sorted(historical)}")

    return output, manifest_sha()


def domain_namespace(handler: str) -> str:
    parts = handler.split(".")
    if len(parts) < 4:
        return "RentalCommand.Data.Atomic.Runtime.Generated"
    if handler.startswith("RentalCommand.Data."):
        return handler.rsplit(".", 1)[0]
    if handler.startswith("RentalCommand.Api."):
        return handler.rsplit(".", 1)[0].replace("RentalCommand.Api", "RentalCommand.Data")
    if handler.startswith("RentalCommand.Engine."):
        return handler.rsplit(".", 1)[0].replace("RentalCommand.Engine", "RentalCommand.Data")
    return "RentalCommand.Data.Atomic.Runtime.Generated"


def generated_type_name(command: str, suffix: str) -> str:
    short = command.rsplit(".", 1)[-1]
    text = re.sub(r"[^A-Za-z0-9_]", "_", short)
    return f"{text}{suffix}"


def identity_policy(item: Registration) -> str:
    if item.identity_literals:
        literals = "|".join(item.identity_literals)
        return (
            "Preserve current caller identity literal or factory token(s): "
            f"{literals}; bind request fingerprint with AtomicCommandFingerprint.Create(command).")
    return (
        "No observed caller identity literal; the canonical direct attempt must derive identity from "
        f"{item.host}:{item.command}:{item.receipt_contract} and bind request fingerprint with AtomicCommandFingerprint.Create(command).")


def write_command_manifest(rows: list[Registration], sha: str) -> str:
    lines = [
        "// <auto-generated />",
        "#nullable enable",
        "",
        "using System.Collections.Generic;",
        "",
        "namespace RentalCommand.Data.Atomic.Runtime.Generated;",
        "",
        "internal sealed record AtomicRuntimeGeneratedCommandMetadata(",
        "    string Host,",
        "    string Registration,",
        "    string CommandType,",
        "    string ResultType,",
        "    string CurrentHandlerType,",
        "    string ReceiptContract,",
        "    string ReplayPolicy,",
        "    string FinalReplayPolicy,",
        "    string StableIdentityAndFingerprintPolicy,",
        "    int CallerCount,",
        "    string HistoricalAttentionDecision);",
        "",
        "internal static class AtomicRuntimeGeneratedCommandManifest",
        "{",
        f"    public const string SourceManifestPath = {csharp(SOURCE_MANIFEST.relative_to(ROOT).as_posix())};",
        f"    public const string SourceManifestSha256 = {csharp(sha)};",
        f"    public const int RegistrationCount = {len(rows)};",
        f"    public const int ApiRegistrationCount = {sum(1 for row in rows if row.host == 'api')};",
        f"    public const int EngineRegistrationCount = {sum(1 for row in rows if row.host == 'engine')};",
        "",
        "    public static IReadOnlyList<AtomicRuntimeGeneratedCommandMetadata> Registrations { get; } =",
        "        new AtomicRuntimeGeneratedCommandMetadata[]",
        "        {",
    ]
    for row in rows:
        lines.extend([
            "            new(",
            f"                {csharp(row.host)},",
            f"                {csharp(row.registration)},",
            f"                {csharp(row.command)},",
            f"                {csharp(row.result)},",
            f"                {csharp(row.current_handler)},",
            f"                {csharp(row.receipt_contract)},",
            f"                {csharp(row.replay_policy)},",
            f"                {csharp(row.final_replay_policy)},",
            f"                {csharp(identity_policy(row))},",
            f"                {row.caller_count},",
            f"                {csharp(row.historical_decision)}),",
        ])
    lines.extend([
        "        };",
        "}",
        "",
    ])
    return "\n".join(lines)


def write_admission_manifest(rows: list[Registration], sha: str) -> str:
    lines = [
        "// <auto-generated />",
        "#nullable enable",
        "",
        "using System.Collections.Generic;",
        "",
        "namespace RentalCommand.Data.Atomic.Runtime.Generated;",
        "",
        "internal sealed record AtomicRuntimeGeneratedAdmissionDependencyMetadata(",
        "    string Host,",
        "    string CommandType,",
        "    string CurrentHandlerType,",
        "    string GeneratedAttemptTypeName,",
        "    string DataOwnerNamespace,",
        "    string ReadPortContract,",
        "    string MutationPortContract,",
        "    string AdmissionDecision,",
        "    string DependencyDecision,",
        "    string QueryProofRequirement,",
        "    string RemoteEffectDecision);",
        "",
        "internal static class AtomicRuntimeGeneratedAdmissionDependencyManifest",
        "{",
        f"    public const string SourceManifestPath = {csharp(SOURCE_MANIFEST.relative_to(ROOT).as_posix())};",
        f"    public const string SourceManifestSha256 = {csharp(sha)};",
        f"    public const int RegistrationCount = {len(rows)};",
        "",
        "    public static IReadOnlyList<AtomicRuntimeGeneratedAdmissionDependencyMetadata> Entries { get; } =",
        "        new AtomicRuntimeGeneratedAdmissionDependencyMetadata[]",
        "        {",
    ]
    for row in rows:
        domain = domain_namespace(row.current_handler)
        lines.extend([
            "            new(",
            f"                {csharp(row.host)},",
            f"                {csharp(row.command)},",
            f"                {csharp(row.current_handler)},",
            f"                {csharp(generated_type_name(row.command, 'AtomicRuntimeAttempt'))},",
            f"                {csharp(domain)},",
            f"                {csharp(generated_type_name(row.command, 'ReadPort'))},",
            f"                {csharp(generated_type_name(row.command, 'MutationPort'))},",
            "                \"metadata_only_dark_registry_entry\",",
            "                \"fresh Data-owned attempt with narrow command-specific ports; no V1 compatibility adapter and no service lookup\",",
            "                \"all authorization, filtering, joining, grouping, sorting, paging, and aggregation must be one database-translated query or view\",",
            "                \"external side effects must be staged through companion outbox intents after the business mutation succeeds\"),",
        ])
    lines.extend([
        "        };",
        "}",
        "",
    ])
    return "\n".join(lines)


def write_simulation_registry(rows: list[Registration], sha: str) -> str:
    simulation = [row for row in rows if row.command in SIMULATION_COMMANDS]
    if [row.command for row in simulation] != list(SIMULATION_COMMANDS):
        raise SystemExit("Simulation registry rows are missing or out of manifest order")

    lines = [
        "// <auto-generated />",
        "#nullable enable",
        "",
        "using RentalCommand.Core.Atomic;",
        "using RentalCommand.Core.Atomic.Runtime;",
        "using RentalCommand.Core.Time;",
        "using RentalCommand.Data.Simulation;",
        "",
        "namespace RentalCommand.Data.Atomic.Runtime.Generated;",
        "",
        "internal sealed class AtomicRuntimeGeneratedSimulationRegistry : AtomicRuntimeGeneratedRegistry",
        "{",
        f"    public const string SourceManifestSha256 = {csharp(sha)};",
        f"    public const int RegistrationCount = {len(simulation)};",
        "",
        "    internal override IAtomicRuntimeAttempt<TCommand, TResult> CreateAttempt<TCommand, TResult>(",
        "        RentalCommandDbContext owner,",
        "        AtomicRuntimeKernel.AtomicRuntimeHostAccessSnapshot access)",
        "    {",
        "        ArgumentNullException.ThrowIfNull(owner);",
        "        ArgumentNullException.ThrowIfNull(access);",
        "        var clock = new AtomicRuntimeDatabaseClockReader(owner);",
        "        var locks = new AtomicRuntimeAdvisoryLock(owner);",
        "        var read = new SimulationRuntimeAccessReadPort(owner, clock);",
        "        var clockMutation = new SimulationClockRuntimeMutationPort(owner, locks, read);",
        "",
    ]
    for row in simulation:
        command_short = row.command.rsplit(".", 1)[-1]
        attempt = command_short.replace("Command", "RuntimeAttempt")
        result_short = row.result.rsplit(".", 1)[-1]
        if command_short == "EnqueueSimulationWorkerCommand":
            lines.extend([
                "        if (typeof(TCommand) == typeof(" + command_short + ")",
                "            && typeof(TResult) == typeof(" + result_short + "))",
                "        {",
                "            return (IAtomicRuntimeAttempt<TCommand, TResult>)(object)",
                f"                new {attempt}(",
                "                    read,",
                "                    new EnqueueSimulationWorkerRuntimeMutationPort(owner, locks, read),",
                "                    access);",
                "        }",
                "",
            ])
            continue
        else:
            constructor_args = "read, clockMutation, access"
        lines.extend([
            "        if (typeof(TCommand) == typeof(" + command_short + ")",
            "            && typeof(TResult) == typeof(" + result_short + "))",
            "        {",
            "            return (IAtomicRuntimeAttempt<TCommand, TResult>)(object)",
            f"                new {attempt}({constructor_args});",
            "        }",
            "",
        ])
    lines.extend([
        "        throw new AtomicRuntimeInvariantException(",
        "            $\"No generated Atomic runtime simulation attempt for {typeof(TCommand).FullName} -> {typeof(TResult).FullName}.\");",
        "    }",
        "}",
        "",
    ])
    return "\n".join(lines)


def write_if_changed(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")


def verify_generated_text(content: str, path: Path) -> None:
    allowed = {"CreateAttempt<"} if path == SIMULATION_REGISTRY_OUTPUT else set()
    for token in FORBIDDEN_GENERATED_TOKENS:
        if token in allowed:
            continue
        if token in content:
            raise SystemExit(f"Forbidden token {token!r} emitted in {path.relative_to(ROOT)}")


def generate() -> dict[str, str]:
    rows, sha = load_registrations()
    command = write_command_manifest(rows, sha)
    admission = write_admission_manifest(rows, sha)
    simulation_registry = write_simulation_registry(rows, sha)
    verify_generated_text(command, COMMAND_OUTPUT)
    verify_generated_text(admission, ADMISSION_OUTPUT)
    verify_generated_text(simulation_registry, SIMULATION_REGISTRY_OUTPUT)
    write_if_changed(COMMAND_OUTPUT, command)
    write_if_changed(ADMISSION_OUTPUT, admission)
    write_if_changed(SIMULATION_REGISTRY_OUTPUT, simulation_registry)
    return {
        COMMAND_OUTPUT.relative_to(ROOT).as_posix(): hashlib.sha256(command.encode()).hexdigest(),
        ADMISSION_OUTPUT.relative_to(ROOT).as_posix(): hashlib.sha256(admission.encode()).hexdigest(),
        SIMULATION_REGISTRY_OUTPUT.relative_to(ROOT).as_posix():
            hashlib.sha256(simulation_registry.encode()).hexdigest(),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    before = {
        path: file.read_bytes() if file.exists() else None
        for path, file in {
            COMMAND_OUTPUT.relative_to(ROOT).as_posix(): COMMAND_OUTPUT,
            ADMISSION_OUTPUT.relative_to(ROOT).as_posix(): ADMISSION_OUTPUT,
            SIMULATION_REGISTRY_OUTPUT.relative_to(ROOT).as_posix(): SIMULATION_REGISTRY_OUTPUT,
        }.items()
    }
    hashes = generate()
    if args.check:
        changed = [
            path for path, file in {
                COMMAND_OUTPUT.relative_to(ROOT).as_posix(): COMMAND_OUTPUT,
                ADMISSION_OUTPUT.relative_to(ROOT).as_posix(): ADMISSION_OUTPUT,
                SIMULATION_REGISTRY_OUTPUT.relative_to(ROOT).as_posix(): SIMULATION_REGISTRY_OUTPUT,
            }.items()
            if before[path] != file.read_bytes()
        ]
        if changed:
            raise SystemExit("Generated files were not byte-stable: " + ", ".join(changed))

    for path, digest in hashes.items():
        print(f"{path} sha256={digest}")
    print(f"registrations={EXPECTED_REGISTRATIONS}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
