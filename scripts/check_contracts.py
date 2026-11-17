"""Validate captured typed-client interactions against immutable published OpenAPI."""
import copy
import hashlib
import json
import re
from pathlib import Path
from urllib.parse import parse_qs

import yaml
from jsonschema import Draft7Validator, Draft202012Validator, FormatChecker

ROOT = Path(__file__).resolve().parents[1]


def json_schema(value):
    if isinstance(value, list):
        return [json_schema(item) for item in value]
    if not isinstance(value, dict):
        return value
    result = {key: json_schema(item) for key, item in value.items() if key != "nullable"}
    if value.get("nullable"):
        return {"anyOf": [result, {"type": "null"}]}
    return result


def validate(schema, body, document):
    complete = copy.deepcopy(schema)
    complete["components"] = document["components"]
    validator = Draft202012Validator if document["openapi"] == "3.1.0" else Draft7Validator
    validator(complete, format_checker=FormatChecker()).validate(body)


def dereference(value, document):
    while "$ref" in value:
        reference = value["$ref"]
        if not reference.startswith("#/"):
            raise ValueError("Only local contract references are supported")
        value = document
        for part in reference[2:].split("/"):
            value = value[part.replace("~1", "/").replace("~0", "~")]
    return value


def main():
    provenance = json.loads((ROOT / "contracts" / "dependencies.json").read_text())
    documents = {}
    for service, source in provenance.items():
        if not re.fullmatch(r"[a-f0-9]{40}", source["commit"]):
            raise ValueError(f"{service}: dependency must be an exact commit")
        raw = (ROOT / "contracts" / source["file"]).read_bytes()
        if hashlib.sha256(raw).hexdigest() != source["sha256"]:
            raise ValueError(f"{service}: published snapshot hash mismatch")
        document = yaml.safe_load(raw)
        if document["openapi"] not in ("3.0.3", "3.1.0"):
            raise ValueError("Unsupported OpenAPI contract dialect")
        documents[service] = json_schema(document) if document["openapi"] == "3.0.3" else document

    messages = json.loads((ROOT / "artifacts" / "consumer-interactions.json").read_text())
    covered = set()
    for message in messages:
        document = documents[message["service"]]
        matches = [
            path for path in document["paths"]
            if re.fullmatch(re.sub(r"\{[^}]+\}", r"[^/]+", path), message["path"])
        ]
        if len(matches) != 1:
            raise ValueError(f"Unknown or ambiguous published route: {message['path']}")
        path = matches[0]
        path_item = document["paths"][path]
        operation = path_item[message["method"]]
        parameters = [
            dereference(parameter, document)
            for parameter in path_item.get("parameters", []) + operation.get("parameters", [])
        ]
        query = parse_qs(message["query"].lstrip("?"))
        declared_query = {parameter["name"]: parameter for parameter in parameters if parameter["in"] == "query"}
        if not set(query).issubset(declared_query):
            raise ValueError("Client sent an undeclared query parameter")
        for name, values in query.items():
            schema = dereference(declared_query[name]["schema"], document)
            value = int(values[0]) if schema.get("type") == "integer" else values[0]
            validate(schema, value, document)
        for parameter in parameters:
            if parameter["in"] == "path":
                index = path.split("/").index("{" + parameter["name"] + "}")
                value = message["path"].split("/")[index]
                schema = dereference(parameter["schema"], document)
                validate(schema, int(value) if schema.get("type") == "integer" else value, document)
        if "requestBody" in operation:
            validate(operation["requestBody"]["content"]["application/json"]["schema"], message["requestBody"], document)
        elif message["requestBody"] is not None:
            raise ValueError("Client sent an undeclared request body")
        response = dereference(operation["responses"][message["status"]], document)
        if "content" in response:
            validate(response["content"]["application/json"]["schema"], message["responseBody"], document)
        elif message["responseBody"] is not None:
            raise ValueError("Unexpected response body")
        covered.add((message["service"], message["method"], path))

    expected = {
        ("students", "get", "/api/v1/students/{id}"),
        ("academics", "get", "/api/v1/courses"),
        ("academics", "get", "/api/v1/courses/{id}"),
        ("enrollments", "get", "/api/v1/enrollments"),
        ("enrollments", "get", "/api/v1/enrollments/{id}"),
        ("enrollments", "post", "/api/v1/enrollments"),
        ("enrollments", "put", "/api/v1/enrollments/{id}"),
        ("enrollments", "delete", "/api/v1/enrollments/{id}"),
    }
    if covered != expected:
        raise ValueError(f"Missing/unexpected consumer interactions: {covered ^ expected}")
    print(f"Validated {len(messages)} real typed-client interactions across {len(documents)} pinned candidate contracts.")


if __name__ == "__main__":
    main()
