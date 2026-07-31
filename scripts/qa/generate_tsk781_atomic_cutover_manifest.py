#!/usr/bin/env python3
"""Generate the TSK-781 atomic cutover manifest from repository source.

The manifest is intentionally static and deterministic. It inventories the
current production atomic surface and its tests before the wholesale cutover,
then fails when the source has duplicate registrations, executable command
callers without handler registration, missing replay policy classification, or
unclassified rows.
"""

from __future__ import annotations

import csv
import json
import re
import subprocess
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


ROOT = Path(__file__).resolve().parents[2]
CSV_PATH = ROOT / "output/qa/tsk781-atomic-cutover-manifest.csv"
JSON_PATH = ROOT / "output/qa/tsk781-atomic-cutover-summary.json"

PRODUCTION_ROOTS = (
    "RentalCommand.Api",
    "RentalCommand.Core",
    "RentalCommand.Data",
    "RentalCommand.Engine",
)
TEST_ROOTS = (
    "RentalCommand.Api.Tests",
    "RentalCommand.Core.Tests",
    "RentalCommand.Data.Tests",
    "RentalCommand.Engine.Tests",
    "RentalCommand.IntegrationTests",
    "RentalCommand.TestCommon",
)
SKIP_PARTS = {"bin", "obj", ".git", "node_modules"}

RECORD_RE = re.compile(
    r"\b(?:(?:public|internal|private|protected)\s+)*"
    r"(?:(?:sealed|abstract|static|partial)\s+)*"
    r"(record|class|interface)\s+([A-Za-z_][A-Za-z0-9_]*)"
    r"(?P<header>[^{};=]*)",
    re.MULTILINE,
)
NAMESPACE_RE = re.compile(r"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;", re.MULTILINE)
REGISTRATION_RE = re.compile(r"AddAtomicCommandHandler\s*<(?P<body>.*?)>\s*\(", re.DOTALL)
EXECUTE_RE = re.compile(r"\.ExecuteAsync\s*<(?P<body>.*?)>\s*\(", re.DOTALL)
ANY_EXECUTE_RE = re.compile(
    r"(?P<receiver>[A-Za-z_][A-Za-z0-9_]*)\.ExecuteAsync\s*"
    r"(?:<(?P<body>.*?)>)?\s*\(",
    re.DOTALL,
)
CLOCK_WRAPPER_EXECUTE_RE = re.compile(r"\bExecuteClockCommandAsync\s*\(", re.DOTALL)
CODEC_RE = re.compile(
    r"new\s+AtomicJsonResultCodec\s*<\s*(?P<result>[^>]+?)\s*>\s*\(\s*\"(?P<contract>[^\"]+)\"",
    re.DOTALL,
)
CODEC_DECL_RE = re.compile(
    r"AtomicJsonResultCodec\s*<\s*(?P<result>[^>]+?)\s*>\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*"
    r"(?:new\s+AtomicJsonResultCodec\s*<\s*(?P<explicit_result>[^>]+?)\s*>\s*)?"
    r"new\s*\(\s*\"(?P<contract>[^\"]+)\"",
    re.DOTALL,
)
CODEC_INLINE_RE = re.compile(
    r"new\s+AtomicJsonResultCodec\s*<\s*(?P<result>[^>]+?)\s*>\s*\(\s*\"(?P<contract>[^\"]+)\"",
    re.DOTALL,
)
COMMAND_VAR_RE = re.compile(
    r"\b(?:var|[A-Za-z_][A-Za-z0-9_.<>?]*)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+"
    r"(?P<type>[A-Za-z_][A-Za-z0-9_.]*Command)\b",
    re.DOTALL,
)
COMMAND_FACTORY_VAR_RE = re.compile(
    r"\b(?:var|[A-Za-z_][A-Za-z0-9_.<>?]*)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:await\s+)?"
    r"(?P<factory>[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*)"
    r"\s*(?:<[^>]+>)?\s*\(",
    re.DOTALL,
)
COMMAND_PARAM_RE = re.compile(
    r"\b(?P<type>TCommand|[A-Za-z_][A-Za-z0-9_.]*Command)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\b",
)
CODEC_PARAM_RE = re.compile(
    r"AtomicJsonResultCodec\s*<\s*(?P<result>[^>]+?)\s*>\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\b",
)
IDENTITY_VAR_RE = re.compile(
    r"\b(?:var|AtomicCommandIdentity)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*"
    r"new\s+AtomicCommandIdentity\s*\(\s*\"(?P<type>[^\"]+)\"",
    re.DOTALL,
)
IDENTITY_FACTORY_VAR_RE = re.compile(
    r"\b(?:var|AtomicCommandIdentity)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*"
    r"(?P<factory>[A-Za-z_][A-Za-z0-9_]*Identity)\s*\(",
    re.DOTALL,
)
COMMAND_FACTORY_RE = re.compile(
    r"\b(?:public|private|internal|protected)\s+static\s+"
    r"(?P<return>[A-Za-z_][A-Za-z0-9_.]*Command)\s+"
    r"(?P<method>Command|[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*\(",
    re.DOTALL,
)
ASYNC_COMMAND_FACTORY_RE = re.compile(
    r"\b(?:public|private|internal|protected)\s+(?:async\s+)?"
    r"Task\s*<\s*(?P<return>[A-Za-z_][A-Za-z0-9_.]*Command)\s*>\s+"
    r"(?P<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
    re.DOTALL,
)
REPLAY_AUTH_RE = re.compile(r"IAtomicReplayAuthorizer\s*<\s*(?P<command>[^>]+?)\s*>")
IDENTITY_RE = re.compile(
    r"new\s+AtomicCommandIdentity\s*\(\s*\"(?P<type>[^\"]+)\"\s*,|"
    r"AtomicCommandIdentity\s*\(\s*\"(?P<type2>[^\"]+)\"\s*,",
    re.DOTALL,
)
DB_REFERENCE_RE = re.compile(
    r"\b(GRANT|REVOKE|CREATE\s+POLICY|ALTER\s+POLICY|DROP\s+POLICY|"
    r"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION|DROP\s+FUNCTION|AtomicCommandReceipts|"
    r"AtomicAuditLogs|set_config|current_setting|rentalcommand_api|rentalcommand_engine)\b",
    re.IGNORECASE,
)
CONTEXT_ESCAPE_PATTERNS = (
    "IAtomicPersistenceSession",
    "IAtomicWriteAttempt",
    "IAtomicInfrastructureUnitOfWork",
    "IAtomicReplayAuthorizer",
    "IAtomicRemoteDependency",
    "IAtomicTransactionSafeDependency",
    "IAtomicExecutionState",
    "IAtomicLockingPersistence",
    "AtomicPersistenceSessionAccessor",
    "AtomicSetBasedCommandGuardInterceptor",
    "InternalSetBasedWriteScope",
    "AtomicCommandAdmission",
    "AtomicInfrastructureUnitOfWork",
    "AtomicCommandDisposition.Joined",
    "AtomicCommandDisposition",
)

COMMAND_RECEIPT_CONTRACT_OVERRIDES = {
    "AtomicMoneyMutationCommand": "money.scoped-mutation.v2",
    "CreateTenantWorkOrderCommand": "portal.work-order.mutation.v2",
    "AddTenantWorkOrderCommentCommand": "portal.work-order.mutation.v2",
    "UpdateTenantWorkOrderCommand": "portal.work-order.mutation.v2",
    "CancelTenantWorkOrderCommand": "portal.work-order.mutation.v2",
    "DecideOwnerApprovalCommand": "owner-portal.approval-decision.v1",
    "ReplyToOwnerMessageCommand": "owner-portal.message-reply.v1",
    "AddEffectivePartyCommand": "lease-management.party.add.v1",
    "EndEffectivePartyCommand": "lease-management.party.end.v1",
    "ChangeEffectivePartyRoleCommand": "lease-management.party.change-role.v1",
    "GrantTenantUserAccessCommand": "lease-management.party.access.grant.v1",
    "EditLeaseAgreementDraftCommand": "lease-agreement.draft.edit.v1",
    "CreateLeaseAgreementSuccessorDraftCommand": "lease-agreement.successor-draft.create.v2",
    "ReplaceIssuedAgreementWithDraftCommand": "lease-agreement.successor-draft.create.v2",
    "VoidLeaseAgreementCommand": "lease-agreement.void.v1",
    "VoidLeaseAddendumCommand": "lease-addendum.void.v1",
}
RETIRED_TARGET_HINTS = (
    "InfrastructureUnitOfWork",
    "RemoteDependency",
    "TransactionSafeDependency",
    "ExecutionState",
    "WriteAttempt",
    "LockingPersistence",
    "PersistenceSession",
    "PersistenceSessionAccessor",
    "SetBasedCommandGuardInterceptor",
    "InternalSetBasedWriteScope",
    "CommandAdmission",
    "Joined",
)
KNOWN_ATOMIC_RECEIVERS = {"_atomic", "_atomicUnitOfWork", "atomic", "Atomic"}


@dataclass(frozen=True)
class SourceFile:
    path: Path
    rel: str
    text: str
    namespace: str


def is_cs_source(path: Path) -> bool:
    return path.suffix == ".cs" and not any(part in SKIP_PARTS for part in path.parts)


def is_under(rel: str, roots: Iterable[str]) -> bool:
    return any(rel == root or rel.startswith(root + "/") for root in roots)


def relpath(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def line_for(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def normalize_type(value: str, namespace: str = "") -> str:
    value = re.sub(r"\s+", " ", value.strip())
    value = value.removeprefix("global::")
    if "." not in value and namespace:
        return f"{namespace}.{value}"
    return value


def short_name(value: str) -> str:
    return value.split(".")[-1].strip()


def generic_parts(body: str) -> list[str]:
    return [re.sub(r"\s+", " ", part.strip()) for part in body.split(",") if part.strip()]


def owner_at(src: SourceFile, offset: int) -> str:
    owner = ""
    for match in RECORD_RE.finditer(src.text):
        if match.start() > offset:
            break
        owner = match.group(2)
    return owner


def find_matching_paren(text: str, open_index: int) -> int:
    depth = 0
    in_string = False
    verbatim = False
    escape = False
    index = open_index
    while index < len(text):
        char = text[index]
        prev = text[index - 1] if index > 0 else ""
        if in_string:
            if escape:
                escape = False
            elif char == "\\" and not verbatim:
                escape = True
            elif char == '"' and (not verbatim or index + 1 >= len(text) or text[index + 1] != '"'):
                in_string = False
                verbatim = False
            elif char == '"' and verbatim and index + 1 < len(text) and text[index + 1] == '"':
                index += 1
        else:
            if char == '"' and prev == "@":
                in_string = True
                verbatim = True
            elif char == '"':
                in_string = True
            elif char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
                if depth == 0:
                    return index
        index += 1
    return -1


def split_top_level_args(text: str) -> list[str]:
    args: list[str] = []
    depth = 0
    start = 0
    in_string = False
    verbatim = False
    escape = False
    index = 0
    while index < len(text):
        char = text[index]
        prev = text[index - 1] if index > 0 else ""
        if in_string:
            if escape:
                escape = False
            elif char == "\\" and not verbatim:
                escape = True
            elif char == '"' and (not verbatim or index + 1 >= len(text) or text[index + 1] != '"'):
                in_string = False
                verbatim = False
            elif char == '"' and verbatim and index + 1 < len(text) and text[index + 1] == '"':
                index += 1
        else:
            if char == '"' and prev == "@":
                in_string = True
                verbatim = True
            elif char == '"':
                in_string = True
            elif char in "([{":
                depth += 1
            elif char in ")]}":
                depth = max(0, depth - 1)
            elif char == "," and depth == 0:
                args.append(text[start:index].strip())
                start = index + 1
        index += 1
    tail = text[start:].strip()
    if tail:
        args.append(tail)
    return args


def discover_codecs(src: SourceFile) -> tuple[list[dict[str, str]], dict[str, tuple[str, str]], dict[str, list[str]]]:
    rows: list[dict[str, str]] = []
    by_name: dict[str, tuple[str, str]] = {}
    contracts_by_result: dict[str, list[str]] = defaultdict(list)
    seen: set[tuple[str, int, str]] = set()
    for match in CODEC_DECL_RE.finditer(src.text):
        result = normalize_type(match.group("explicit_result") or match.group("result"), src.namespace)
        name = match.group("name")
        owner = owner_at(src, match.start())
        contract = match.group("contract")
        seen.add((result, line_for(src.text, match.start()), contract))
        by_name[name] = (result, contract)
        if owner:
            by_name[f"{owner}.{name}"] = (result, contract)
        contracts_by_result[short_name(result)].append(contract)
        add_row(
            rows,
            category="receipt_contract_codec",
            kind="atomic_json_result_codec_declaration",
            symbol=name,
            path=src.rel,
            line=line_for(src.text, match.start()),
            classification="retain_exact_result_codec_contract",
            target="preserve_receipt_contract_name_and_replay_parity",
            result=result,
            replay_policy="exact_stored_json_result_replay_required",
            receipt_contract=contract,
            notes="AtomicJsonResultCodec declaration contract name.",
        )
    for match in CODEC_INLINE_RE.finditer(src.text):
        result = normalize_type(match.group("result"), src.namespace)
        contract = match.group("contract")
        key = (result, line_for(src.text, match.start()), contract)
        if key in seen:
            continue
        contracts_by_result[short_name(result)].append(contract)
        add_row(
            rows,
            category="receipt_contract_codec",
            kind="atomic_json_result_codec_inline",
            symbol=result,
            path=src.rel,
            line=line_for(src.text, match.start()),
            classification="retain_exact_result_codec_contract",
            target="preserve_receipt_contract_name_and_replay_parity",
            result=result,
            replay_policy="exact_stored_json_result_replay_required",
            receipt_contract=contract,
            notes="Inline AtomicJsonResultCodec contract name.",
        )
    return rows, by_name, contracts_by_result


def discover_command_variables(
    src: SourceFile,
    command_factories: dict[str, str],
    before_offset: int | None = None,
) -> dict[str, str]:
    variables: dict[str, str] = {}
    for match in COMMAND_VAR_RE.finditer(src.text):
        if before_offset is not None and match.start() > before_offset:
            break
        variables[match.group("name")] = normalize_type(match.group("type"), src.namespace)
    for match in COMMAND_FACTORY_VAR_RE.finditer(src.text):
        if before_offset is not None and match.start() > before_offset:
            break
        factory = match.group("factory")
        factory_short = ".".join(factory.split(".")[-2:])
        if factory in command_factories:
            variables[match.group("name")] = command_factories[factory]
        elif factory_short in command_factories:
            variables[match.group("name")] = command_factories[factory_short]
    for match in COMMAND_PARAM_RE.finditer(src.text):
        if before_offset is not None and match.start() > before_offset:
            break
        variables.setdefault(match.group("name"), normalize_type(match.group("type"), src.namespace))
    return variables


def discover_command_factories(files: list[SourceFile]) -> dict[str, str]:
    factories: dict[str, str] = {}
    for src in files:
        if not is_under(src.rel, PRODUCTION_ROOTS):
            continue
        for match in COMMAND_FACTORY_RE.finditer(src.text):
            owner = owner_at(src, match.start())
            if not owner:
                continue
            factories[f"{owner}.{match.group('method')}"] = normalize_type(match.group("return"), src.namespace)
        for match in ASYNC_COMMAND_FACTORY_RE.finditer(src.text):
            factories[match.group("method")] = normalize_type(match.group("return"), src.namespace)
    return factories


def discover_identity_variables(src: SourceFile, before_offset: int | None = None) -> dict[str, str]:
    variables: dict[str, str] = {}
    for match in IDENTITY_VAR_RE.finditer(src.text):
        if before_offset is not None and match.start() > before_offset:
            break
        variables[match.group("name")] = match.group("type")
    for match in IDENTITY_FACTORY_VAR_RE.finditer(src.text):
        if before_offset is not None and match.start() > before_offset:
            break
        variables[match.group("name")] = f"factory:{match.group('factory')}"
    return variables


def discover_codec_parameters(src: SourceFile, before_offset: int | None = None) -> dict[str, str]:
    variables: dict[str, str] = {}
    for match in CODEC_PARAM_RE.finditer(src.text):
        if before_offset is not None and match.start() > before_offset:
            break
        variables[match.group("name")] = normalize_type(match.group("result"), src.namespace)
    return variables


def infer_command(
    arg: str,
    command_vars: dict[str, str],
    command_factories: dict[str, str],
    namespace: str,
) -> str:
    arg = arg.strip()
    new_match = re.search(r"\bnew\s+(?P<type>[A-Za-z_][A-Za-z0-9_.]*Command)\b", arg)
    if new_match:
        return normalize_type(new_match.group("type"), namespace)
    factory_match = re.search(r"\b(?P<type>[A-Za-z_][A-Za-z0-9_.]*Command)\.", arg)
    if factory_match:
        return normalize_type(factory_match.group("type"), namespace)
    named_factory_match = re.search(r"\b(?P<factory>[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z_][A-Za-z0-9_]*)\s*\(", arg)
    if named_factory_match:
        factory = named_factory_match.group("factory")
        if factory in command_factories:
            return command_factories[factory]
        factory_short = ".".join(factory.split(".")[-2:])
        if factory_short in command_factories:
            return command_factories[factory_short]
        if factory.endswith(".Command"):
            owner = factory.split(".")[-2]
            return normalize_type(f"{owner}Command", namespace)
    await_factory_match = re.search(r"\b(?P<factory>[A-Za-z_][A-Za-z0-9_]*)\s*\(", arg)
    if await_factory_match and await_factory_match.group("factory") in command_factories:
        return command_factories[await_factory_match.group("factory")]
    if arg in command_vars:
        return command_vars[arg]
    if (arg.endswith("Command") and arg[:1].isupper()) or arg == "TCommand":
        return normalize_type(arg, namespace)
    creator_match = re.search(r"\bcreateCommand\s*\(", arg)
    if creator_match:
        return "TCommand"
    return ""


def infer_identity(arg: str, identity_vars: dict[str, str]) -> str:
    literal = arg.strip()
    if len(literal) >= 2 and literal[0] == '"' and literal[-1] == '"':
        return literal[1:-1]
    literal_match = re.search(r"AtomicCommandIdentity\s*\(\s*\"(?P<type>[^\"]+)\"", arg)
    if literal_match:
        return literal_match.group("type")
    if arg in identity_vars:
        return identity_vars[arg]
    if "commandType" in arg:
        return "parameter:commandType"
    if re.search(r"\bIdentity\s*\(", arg):
        return "factory:Identity"
    if re.search(r"\b[A-Za-z_][A-Za-z0-9_.]*Identity\s*\(", arg):
        return "factory:" + arg.split("(", 1)[0].strip()
    if re.search(r"\b[A-Za-z_][A-Za-z0-9_.]*\.Identity\s*\(", arg):
        return "factory:" + arg.split("(", 1)[0].strip()
    if re.search(r"\b[A-Za-z_][A-Za-z0-9_.]*\.For[A-Za-z0-9_]*\s*\(", arg):
        return "factory:" + arg.split("(", 1)[0].strip()
    if "CommandIdentity" in arg and re.search(r"\b[A-Za-z_][A-Za-z0-9_.]*\.Create\s*\(", arg):
        return "factory:" + arg.split("(", 1)[0].strip()
    return ""


def is_dev_clock_generic_wrapper_body(src: SourceFile, offset: int) -> bool:
    if src.rel != "RentalCommand.Api/Controllers/DevClockController.cs":
        return False

    start = src.text.find("ExecuteClockCommandAsync<TCommand>")
    if start < 0 or offset < start:
        return False

    end = src.text.find("private bool TryBuildCommandContext", start)
    return end < 0 or offset < end


def infer_codec(
    arg: str,
    codec_by_name: dict[str, tuple[str, str]],
    codec_params: dict[str, str],
    namespace: str,
) -> tuple[str, str]:
    arg = arg.strip()
    inline_match = CODEC_INLINE_RE.search(arg)
    if inline_match:
        result = normalize_type(inline_match.group("result"), namespace)
        return result, inline_match.group("contract")
    target_match = re.search(r"new\s*\(\s*\"(?P<contract>[^\"]+)\"", arg)
    if target_match:
        return "", target_match.group("contract")
    if arg in codec_by_name:
        return codec_by_name[arg]
    if arg in codec_params:
        return codec_params[arg], f"parameter:{arg}"
    if arg.endswith("Codec") or arg == "codec":
        return "", f"variable:{arg}"
    return "", ""


def source_files() -> list[SourceFile]:
    files: list[SourceFile] = []
    for path in sorted(ROOT.rglob("*.cs")):
        if not is_cs_source(path):
            continue
        rel = relpath(path)
        if not is_under(rel, PRODUCTION_ROOTS + TEST_ROOTS):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        namespace = (NAMESPACE_RE.search(text) or [None, ""])[1]
        files.append(SourceFile(path, rel, text, namespace))
    return files


def add_row(rows: list[dict[str, str]], **kwargs: str) -> None:
    row = {
        "category": "",
        "kind": "",
        "symbol": "",
        "path": "",
        "line": "",
        "classification": "",
        "target": "",
        "registered_in": "",
        "handler": "",
        "result": "",
        "caller_count": "0",
        "replay_policy": "",
        "receipt_contract": "",
        "notes": "",
    }
    row.update({key: str(value) for key, value in kwargs.items()})
    rows.append(row)


def discover_registrations(files: list[SourceFile]) -> tuple[list[dict[str, str]], set[str], Counter]:
    rows: list[dict[str, str]] = []
    registered_commands: set[str] = set()
    registration_keys: Counter = Counter()
    for src in files:
        if not src.rel.endswith("Program.cs"):
            continue
        host = "api" if src.rel.startswith("RentalCommand.Api/") else "engine"
        for match in REGISTRATION_RE.finditer(src.text):
            parts = generic_parts(match.group("body"))
            if len(parts) != 3:
                continue
            command, result, handler = parts
            command = normalize_type(command, src.namespace)
            result = normalize_type(result, src.namespace)
            handler = normalize_type(handler, src.namespace)
            registered_commands.add(short_name(command))
            registration_keys[(host, command, result)] += 1
            add_row(
                rows,
                category="api_engine_registration",
                kind=f"{host}_handler_registration",
                symbol=command,
                path=src.rel,
                line=line_for(src.text, match.start()),
                classification="retain_handler_registration_until_atomic_cutover",
                target="replace_with_single_generated_capability_manifest_registration",
                registered_in=host,
                handler=handler,
                result=result,
                replay_policy="fold_replay_authorization_into_mandatory_handler_policy",
                notes="Executable registration must have exactly one handler per host/command/result.",
            )
    return rows, registered_commands, registration_keys


def discover_symbols(files: list[SourceFile], registered_short_names: set[str]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for src in files:
        if not is_under(src.rel, PRODUCTION_ROOTS):
            continue
        for match in RECORD_RE.finditer(src.text):
            header = match.group("header")
            symbol = normalize_type(match.group(2), src.namespace)
            name = short_name(symbol)
            interfaces = header.split(":", 1)[1] if ":" in header else ""
            if "IAtomicCommandData" in interfaces:
                executable = name in registered_short_names or name.endswith("Command")
                classification = (
                    "retain_typed_command_identity_until_cutover"
                    if executable
                    else "retain_nested_command_value_object_until_parent_cutover"
                )
                target = (
                    "typed_command_contract_in_new_runner_manifest"
                    if executable
                    else "fold_into_parent_command_payload_contract"
                )
                add_row(
                    rows,
                    category="command_result_contract",
                    kind="atomic_command_data",
                    symbol=symbol,
                    path=src.rel,
                    line=line_for(src.text, match.start()),
                    classification=classification,
                    target=target,
                    replay_policy="fold_replay_authorization_into_mandatory_handler_policy"
                    if executable
                    else "inherits_parent_command_replay_policy",
                    notes="Production command DTO discovered by IAtomicCommandData marker.",
                )
            if "IAtomicResultData" in interfaces:
                add_row(
                    rows,
                    category="command_result_contract",
                    kind="atomic_result_data",
                    symbol=symbol,
                    path=src.rel,
                    line=line_for(src.text, match.start()),
                    classification="retain_typed_outcome_until_cutover",
                    target="typed_result_contract_in_new_runner_manifest",
                    replay_policy="exact_result_replay_contract_required",
                    notes="Production result DTO discovered by IAtomicResultData marker.",
                )
            for pattern in CONTEXT_ESCAPE_PATTERNS:
                if pattern in header and name != pattern:
                    target = (
                        "replace_with_one_small_execution_context"
                        if any(hint in name for hint in RETIRED_TARGET_HINTS)
                        else "move_semantics_to_owning_domain_or_handler_policy"
                    )
                    add_row(
                        rows,
                        category="persistence_context_escape",
                        kind="atomic_surface_symbol",
                        symbol=symbol,
                        path=src.rel,
                        line=line_for(src.text, match.start()),
                        classification="delete_or_fold_retired_atomic_surface",
                        target=target,
                        replay_policy="not_receipt_replay_surface",
                        notes=f"References {pattern}; classify before whole-layer replacement.",
                    )
    return rows


def discover_replay_authorizers(files: list[SourceFile]) -> tuple[list[dict[str, str]], set[str]]:
    rows: list[dict[str, str]] = []
    commands: set[str] = set()
    for src in files:
        if not is_under(src.rel, PRODUCTION_ROOTS):
            continue
        for match in REPLAY_AUTH_RE.finditer(src.text):
            command = normalize_type(match.group("command"), src.namespace)
            commands.add(short_name(command))
            add_row(
                rows,
                category="replay_policy",
                kind="replay_authorizer",
                symbol=command,
                path=src.rel,
                line=line_for(src.text, match.start()),
                classification="fold_replay_authorizer_into_mandatory_handler_policy",
                target="delete_separate_replay_authorizer_interface_and_implementations",
                replay_policy="handler_policy_required_before_returning_stored_result",
                notes="Separate IAtomicReplayAuthorizer usage must not survive cutover.",
            )
    return rows, commands


def discover_callers_and_codecs(
    files: list[SourceFile],
    registered_short_names: set[str],
) -> tuple[list[dict[str, str]], Counter, dict[str, list[str]], dict[str, list[str]]]:
    rows: list[dict[str, str]] = []
    caller_counts: Counter = Counter()
    contracts_by_result: dict[str, list[str]] = defaultdict(list)
    contracts_by_command: dict[str, list[str]] = defaultdict(list)
    command_factories = discover_command_factories(files)
    codec_maps_by_file: dict[str, dict[str, tuple[str, str]]] = {}
    global_codec_by_name: dict[str, tuple[str, str]] = {}
    for src in files:
        if not is_under(src.rel, PRODUCTION_ROOTS):
            continue
        codec_rows, codec_by_name, file_contracts_by_result = discover_codecs(src)
        rows.extend(codec_rows)
        codec_maps_by_file[src.rel] = codec_by_name
        global_codec_by_name.update(codec_by_name)
        for result, contracts in file_contracts_by_result.items():
            contracts_by_result[result].extend(contracts)
    for src in files:
        if not is_under(src.rel, PRODUCTION_ROOTS):
            continue
        codec_by_name = {**global_codec_by_name, **codec_maps_by_file.get(src.rel, {})}
        for match in ANY_EXECUTE_RE.finditer(src.text):
            receiver = match.group("receiver")
            if receiver not in KNOWN_ATOMIC_RECEIVERS:
                continue
            if is_dev_clock_generic_wrapper_body(src, match.start()):
                continue
            command_vars = discover_command_variables(src, command_factories, match.start())
            identity_vars = discover_identity_variables(src, match.start())
            codec_params = discover_codec_parameters(src, match.start())
            open_index = src.text.find("(", match.end() - 1)
            close_index = find_matching_paren(src.text, open_index)
            if open_index < 0 or close_index < 0:
                add_row(
                    rows,
                    category="caller",
                    kind="unresolved_atomic_execute_call",
                    symbol=f"{receiver}.ExecuteAsync",
                    path=src.rel,
                    line=line_for(src.text, match.start()),
                    classification="retain_call_site_until_coordinated_cutover",
                    target="replace_with_single_public_command_execution_method",
                    replay_policy="unresolved_call_must_be_classified_before_cutover",
                    notes="Could not parse balanced ExecuteAsync arguments.",
                )
                continue
            args = split_top_level_args(src.text[open_index + 1:close_index])
            generic = match.group("body")
            command = ""
            result = ""
            identity = ""
            contract = ""
            if generic:
                parts = generic_parts(generic)
                if len(parts) >= 2:
                    command = normalize_type(parts[0], src.namespace)
                    result = normalize_type(parts[1], src.namespace)
            if len(args) >= 1:
                identity = infer_identity(args[0], identity_vars)
            if len(args) >= 2 and not command:
                command = infer_command(args[1], command_vars, command_factories, src.namespace)
            if len(args) >= 3:
                codec_result, contract = infer_codec(args[2], codec_by_name, codec_params, src.namespace)
                if codec_result and not result:
                    result = codec_result
            if not command and result:
                result_short = short_name(result)
                candidates = []
                if result_short.endswith("Result"):
                    candidates.append(result_short[:-6] + "Command")
                if result_short.endswith("MutationResult"):
                    candidates.append(result_short[:-6] + "Command")
                for candidate in candidates:
                    if candidate in registered_short_names:
                        command = normalize_type(candidate, src.namespace)
                        break
            kind = "atomic_execute_call"
            unresolved_parts: list[str] = []
            if not command:
                unresolved_parts.append("command")
            if not identity:
                unresolved_parts.append("identity")
            if not result:
                unresolved_parts.append("result")
            if not contract:
                unresolved_parts.append("codec")
            if unresolved_parts:
                kind = "unresolved_atomic_execute_call"
            symbol = command or f"{receiver}.ExecuteAsync"
            if command:
                caller_counts[short_name(command)] += 1
            if command and contract and not contract.startswith("variable:"):
                contracts_by_command[short_name(command)].append(contract)
            add_row(
                rows,
                category="caller",
                kind=kind,
                symbol=symbol,
                path=src.rel,
                line=line_for(src.text, match.start()),
                classification="retain_call_site_until_coordinated_cutover",
                target="replace_with_single_public_command_execution_method",
                result=result,
                replay_policy="caller_supplies_identity_handler_policy_authorizes_replay",
                receipt_contract=contract,
                notes=(
                    f"IAtomicUnitOfWork.ExecuteAsync caller via {receiver}; identity={identity or 'unresolved'}; "
                    f"unresolved={','.join(unresolved_parts) if unresolved_parts else 'none'}."
                ),
            )
        for match in CLOCK_WRAPPER_EXECUTE_RE.finditer(src.text):
            command_vars = discover_command_variables(src, command_factories, match.start())
            identity_vars = discover_identity_variables(src, match.start())
            open_index = src.text.find("(", match.end() - 1)
            close_index = find_matching_paren(src.text, open_index)
            if open_index < 0 or close_index < 0:
                add_row(
                    rows,
                    category="caller",
                    kind="unresolved_atomic_execute_call",
                    symbol="ExecuteClockCommandAsync",
                    path=src.rel,
                    line=line_for(src.text, match.start()),
                    classification="retain_call_site_until_coordinated_cutover",
                    target="replace_with_single_public_command_execution_method",
                    replay_policy="unresolved_call_must_be_classified_before_cutover",
                    notes="Could not parse balanced ExecuteClockCommandAsync arguments.",
                )
                continue

            args = split_top_level_args(src.text[open_index + 1:close_index])
            identity = infer_identity(args[0], identity_vars) if len(args) >= 1 else ""
            command = infer_command(args[3], command_vars, command_factories, src.namespace) if len(args) >= 4 else ""
            result = "RentalCommand.Core.Time.SimulationClockMutationResult"
            contract = "simulation.clock.mutation.v1"
            unresolved_parts: list[str] = []
            if not command:
                unresolved_parts.append("command")
            if not identity:
                unresolved_parts.append("identity")
            kind = "unresolved_atomic_execute_call" if unresolved_parts else "atomic_execute_call"
            symbol = command or "ExecuteClockCommandAsync"
            if command:
                caller_counts[short_name(command)] += 1
                contracts_by_command[short_name(command)].append(contract)
            add_row(
                rows,
                category="caller",
                kind=kind,
                symbol=symbol,
                path=src.rel,
                line=line_for(src.text, match.start()),
                classification="retain_call_site_until_coordinated_cutover",
                target="replace_with_single_public_command_execution_method",
                result=result,
                replay_policy="caller_supplies_identity_handler_policy_authorizes_replay",
                receipt_contract=contract,
                notes=(
                    "Generic ExecuteClockCommandAsync caller; "
                    f"identity={identity or 'unresolved'}; "
                    f"unresolved={','.join(unresolved_parts) if unresolved_parts else 'none'}."
                ),
            )
        for match in IDENTITY_RE.finditer(src.text):
            command_type = match.group("type") or match.group("type2")
            add_row(
                rows,
                category="caller",
                kind="atomic_command_identity_literal",
                symbol=command_type,
                path=src.rel,
                line=line_for(src.text, match.start()),
                classification="retain_stable_command_identity_until_cutover",
                target="typed_identity_in_new_runner_manifest",
                replay_policy="identity_fingerprint_conflict_policy_required",
                notes="AtomicCommandIdentity command type literal.",
            )
    return rows, caller_counts, contracts_by_result, contracts_by_command


def discover_fingerprint_ignores(files: list[SourceFile]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for src in files:
        if not is_under(src.rel, PRODUCTION_ROOTS):
            continue
        for index, line in enumerate(src.text.splitlines(), start=1):
            if "AtomicFingerprintIgnore" not in line:
                continue
            add_row(
                rows,
                category="fingerprint_contract",
                kind="fingerprint_ignore_annotation",
                symbol=line.strip(),
                path=src.rel,
                line=index,
                classification="retain_explicit_fingerprint_exclusion_until_reviewed",
                target="carry_or_remove_by_command_specific_identity_policy",
                replay_policy="request_fingerprint_policy_must_name_excluded_fields",
                notes="Live annotation; do not assume zero ignored fingerprint fields.",
            )
    return rows


def discover_db_references(files: list[SourceFile]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for src in files:
        if not is_under(src.rel, ("RentalCommand.Data",)):
            continue
        for index, line in enumerate(src.text.splitlines(), start=1):
            if not DB_REFERENCE_RE.search(line):
                continue
            lowered = line.lower()
            if "grant" in lowered or "revoke" in lowered:
                target = "move_grant_diff_into_cutover_db_manifest"
                kind = "db_grant_or_revoke_reference"
            elif "policy" in lowered:
                target = "move_policy_diff_into_cutover_db_manifest"
                kind = "db_policy_reference"
            elif "function" in lowered:
                target = "move_function_diff_into_cutover_db_manifest"
                kind = "db_function_reference"
            else:
                target = "preserve_atomic_receipt_audit_schema_contract"
                kind = "db_atomic_contract_reference"
            add_row(
                rows,
                category="db_contract",
                kind=kind,
                symbol=line.strip()[:180],
                path=src.rel,
                line=index,
                classification="retain_db_contract_until_versioned_cutover",
                target=target,
                replay_policy="db_authorization_and_receipt_replay_invariant_required",
                notes="DB-side grant/policy/function/receipt/audit reference.",
            )
    return rows


def discover_tests(files: list[SourceFile]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for src in files:
        if not is_under(src.rel, TEST_ROOTS):
            continue
        if "Atomic" not in src.rel and "atomic" not in src.text:
            continue
        add_row(
            rows,
            category="test_fixture",
            kind="atomic_test_fixture",
            symbol=Path(src.rel).name,
            path=src.rel,
            line=1,
            classification="retain_characterization_until_cutover_proof_replaces_it",
            target="replace_or_delete_with_new_runner_parity_tests",
            replay_policy="tests_must_cover_replay_conflict_authorization_rollback_db_side_sql",
            notes="Atomic-related test fixture or contract test.",
        )
    return rows


def discover_deleted_atomic_files() -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    status = subprocess.run(
        ["git", "status", "--porcelain"],
        cwd=ROOT,
        text=True,
        check=True,
        capture_output=True,
    ).stdout.splitlines()
    for line in status:
        if len(line) < 4:
            continue
        code = line[:2]
        path = line[3:]
        if " -> " in path:
            path = path.split(" -> ", 1)[1]
        if "D" not in code or "Atomic" not in path:
            continue
        add_row(
            rows,
            category="deleted_current_worktree_file",
            kind="deleted_atomic_file",
            symbol=Path(path).name,
            path=path,
            line=1,
            classification="delete_retired_atomic_surface",
            target="confirm_zero_reference_or_domain_owned_replacement",
            replay_policy="not_receipt_replay_surface",
            notes=f"Deleted in current worktree status {code}; included so cutover inventory tracks removal.",
        )
    return rows


def enrich_rows(
    rows: list[dict[str, str]],
    caller_counts: Counter,
    contracts_by_result: dict[str, list[str]],
    contracts_by_command: dict[str, list[str]],
    replay_authorized: set[str],
) -> None:
    for row in rows:
        symbol_short = short_name(row["symbol"])
        result_short = short_name(row["result"])
        if row["category"] in {"api_engine_registration", "command_result_contract"}:
            row["caller_count"] = str(caller_counts.get(symbol_short, 0))
        if row["category"] == "api_engine_registration" and not row["receipt_contract"]:
            if symbol_short in COMMAND_RECEIPT_CONTRACT_OVERRIDES:
                row["receipt_contract"] = COMMAND_RECEIPT_CONTRACT_OVERRIDES[symbol_short]
                continue
            command_contracts = sorted(set(contracts_by_command.get(symbol_short, [])))
            result_contracts = sorted(set(contracts_by_result.get(result_short, [])))
            if len(command_contracts) == 1:
                row["receipt_contract"] = command_contracts[0]
            elif len(result_contracts) == 1:
                row["receipt_contract"] = result_contracts[0]
            elif len(command_contracts) > 1:
                row["receipt_contract"] = "split_receipt_contracts:" + "|".join(command_contracts)
            elif len(result_contracts) > 1:
                row["receipt_contract"] = "shared_result_type_requires_command_contract_override:" + "|".join(result_contracts)
            elif caller_counts.get(symbol_short, 0) == 0:
                row["receipt_contract"] = "no_observed_execute_call_site"
        elif result_short and not row["receipt_contract"]:
            result_contracts = sorted(set(contracts_by_result.get(result_short, [])))
            if len(result_contracts) == 1:
                row["receipt_contract"] = result_contracts[0]
            elif len(result_contracts) > 1:
                row["receipt_contract"] = "multiple_result_contracts:" + "|".join(result_contracts)
        if row["category"] == "api_engine_registration":
            if symbol_short in replay_authorized:
                row["replay_policy"] = "existing_authorizer_to_fold_into_handler_policy"
            else:
                row["replay_policy"] = "handler_policy_must_authorize_new_and_replayed_attempts"


def validate(
    rows: list[dict[str, str]],
    registration_keys: Counter,
    registered_short_names: set[str],
    caller_counts: Counter,
) -> list[str]:
    failures: list[str] = []
    duplicate_registrations = [key for key, count in registration_keys.items() if count > 1]
    if duplicate_registrations:
        failures.append(f"duplicate registrations: {len(duplicate_registrations)}")

    missing_registered_callers = sorted(
        command for command, count in caller_counts.items()
        if count > 0 and command not in registered_short_names and command != "TCommand"
    )
    if missing_registered_callers:
        failures.append(
            "missing handler registration for executable callers: "
            + ", ".join(missing_registered_callers[:30])
            + (" ..." if len(missing_registered_callers) > 30 else "")
        )

    missing_replay_policy = [
        row for row in rows
        if row["category"] in {"api_engine_registration", "caller", "command_result_contract"}
        and not row["replay_policy"].strip()
    ]
    if missing_replay_policy:
        failures.append(f"missing explicit replay policy rows: {len(missing_replay_policy)}")

    unclassified = [
        row for row in rows
        if not row["classification"].strip() or not row["target"].strip()
    ]
    if unclassified:
        failures.append(f"unclassified rows: {len(unclassified)}")

    unresolved_calls = [
        row for row in rows
        if row["category"] == "caller"
        and row["kind"] == "unresolved_atomic_execute_call"
        and short_name(row["symbol"]) != "TCommand"
    ]
    if unresolved_calls:
        failures.append(f"unresolved atomic runner calls: {len(unresolved_calls)}")

    registration_codec_failures = [
        row for row in rows
        if row["category"] == "api_engine_registration"
        and (
            not row["receipt_contract"].strip()
            or row["receipt_contract"].startswith("multiple:")
            or row["receipt_contract"].startswith("multiple_result_contracts:")
            or row["receipt_contract"] == "no_observed_execute_call_site"
            or row["receipt_contract"].startswith("split_receipt_contracts:")
            or row["receipt_contract"].startswith("shared_result_type_requires_command_contract_override:")
        )
    ]
    if registration_codec_failures:
        failures.append(f"registered commands without exactly one codec/result contract: {len(registration_codec_failures)}")
    return failures


def write_outputs(rows: list[dict[str, str]], failures: list[str]) -> dict[str, object]:
    CSV_PATH.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = [
        "category",
        "kind",
        "symbol",
        "path",
        "line",
        "classification",
        "target",
        "registered_in",
        "handler",
        "result",
        "caller_count",
        "replay_policy",
        "receipt_contract",
        "notes",
    ]
    rows.sort(key=lambda row: (row["category"], row["path"], int(row["line"] or "0"), row["kind"], row["symbol"]))
    by_category = Counter(row["category"] for row in rows)
    by_classification = Counter(row["classification"] for row in rows)
    registration_attention = [
        {
            "symbol": row["symbol"],
            "path": row["path"],
            "line": row["line"],
            "caller_count": row["caller_count"],
            "result": row["result"],
            "receipt_contract": row["receipt_contract"],
        }
        for row in rows
        if row["category"] == "api_engine_registration"
        and (
            row["receipt_contract"] == "no_observed_execute_call_site"
            or row["receipt_contract"].startswith("split_receipt_contracts:")
            or row["receipt_contract"].startswith("shared_result_type_requires_command_contract_override:")
        )
    ]
    summary: dict[str, object] = {
        "task": "TSK-781",
        "source": "static repository scan",
        "csv_path": relpath(CSV_PATH),
        "total_rows": len(rows),
        "counts_by_category": dict(sorted(by_category.items())),
        "counts_by_classification": dict(sorted(by_classification.items())),
        "registration_contract_attention": registration_attention,
        "failures": failures,
    }
    if not failures:
        with CSV_PATH.open("w", encoding="utf-8", newline="") as output:
            writer = csv.DictWriter(output, fieldnames=fieldnames, extrasaction="ignore", lineterminator="\n")
            writer.writeheader()
            writer.writerows(rows)
        JSON_PATH.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return summary


def main() -> int:
    files = source_files()
    rows: list[dict[str, str]] = []

    registration_rows, registered_short_names, registration_keys = discover_registrations(files)
    rows.extend(registration_rows)
    replay_rows, replay_authorized = discover_replay_authorizers(files)
    rows.extend(replay_rows)
    caller_rows, caller_counts, contracts_by_result, contracts_by_command = discover_callers_and_codecs(
        files,
        registered_short_names,
    )
    rows.extend(caller_rows)
    rows.extend(discover_symbols(files, registered_short_names))
    rows.extend(discover_fingerprint_ignores(files))
    rows.extend(discover_db_references(files))
    rows.extend(discover_tests(files))
    rows.extend(discover_deleted_atomic_files())

    enrich_rows(rows, caller_counts, contracts_by_result, contracts_by_command, replay_authorized)
    failures = validate(rows, registration_keys, registered_short_names, caller_counts)
    summary = write_outputs(rows, failures)

    print(json.dumps(summary, indent=2, sort_keys=True))
    if failures:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
