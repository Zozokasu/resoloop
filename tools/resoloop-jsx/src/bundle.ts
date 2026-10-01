import * as fs from "node:fs";
import * as path from "node:path";
import { randomUUID } from "node:crypto";
import type { ApplyDocument } from "./evaluate.js";
import { hashText, verifyInputs, type InputFile } from "./input-snapshot.js";
import { originOfIr, unknown, type Origin, type Source } from "./source-map.js";

export function reserveBundleOutput(output: string): string {
  const final = path.resolve(output);
  const parent = path.dirname(final);
  if (fs.existsSync(final) || fs.existsSync(parent)) throw new Error("Bundle output requires a new directory; existing outputs/directories cannot be reused.");
  fs.mkdirSync(path.dirname(parent), { recursive: true });
  fs.mkdirSync(parent); // exclusive creation, including competing producers
  return final;
}

export function publishBundle(document: ApplyDocument, inputs: InputFile[], catalogText: string, buildId: string, output: string): void {
  const catalog = JSON.parse(catalogText);
  const types = new Set<string>();
  const entries: any[] = [];
  const add = (jsonPath: string, pathSegments: (string | number)[], entityKind: string, key: string,
    member: string | undefined, origin: Origin | undefined, related: Source[] = []): void => {
    entries.push({ jsonPath, pathSegments, entityKind, key, ...(member === undefined ? {} : { member }),
      source: origin?.source ?? unknown, valueSource: origin?.valueSource ?? unknown,
      related: [...new Map([...(origin?.related ?? []), ...related].filter(s => s.status === "known").map(s => [JSON.stringify(s), s])).values()] });
  };
  const values = (value: any, p: string, segments: (string | number)[], key: string, member: string,
    origin: Origin | undefined, related: Source[]): void => {
    add(p, segments, "component", key, member, origin, related);
    if (Array.isArray(value)) value.forEach((v, i) => values(v, `${p}[${i}]`, [...segments, i], key, member, origin?.children?.[String(i)], related));
    else if (value !== null && typeof value === "object") for (const [name, v] of Object.entries(value))
      values(v, p + "[" + JSON.stringify(name) + "]", [...segments, name], key, member, origin?.children?.[name], related);
  };
  const walk = (node: any, jsonPath: string, segments: (string | number)[]): void => {
    const slot = originOfIr(node.slot);
    add(jsonPath + ".slot", [...segments, "slot"], "slot", node.slot.key, undefined, slot && { source: slot.source });
    for (const property of Object.keys(node.slot ?? {}).sort()) add(jsonPath + ".slot[" + JSON.stringify(property) + "]", [...segments, "slot", property], "slot", node.slot.key, property, slot?.attributes[property], slot?.related);
    for (const [index, component] of (node.components ?? []).entries()) {
      const p = `${jsonPath}.components[${index}]`;
      const full = catalog.content?.aliases?.[component.type] ?? component.type;
      // Identification only. Core is the sole semantic/evidence validator.
      types.add(catalog.content?.types?.find((t: any) => t.fullName === full && t.confirmed)?.fullName ?? component.type);
      const origin = originOfIr(component);
      const s = [...segments, "components", index];
      add(p, s, "component", component.key, undefined, origin && { source: origin.source }, origin?.related);
      add(p + ".type", [...s, "type"], "component", component.key, undefined, origin?.attributes.type, origin?.related);
      for (const section of ["fields", "initialFields"])
        for (const field of Object.keys(component[section] ?? {}).sort()) values(component[section][field], p + "." + section + "[" + JSON.stringify(field) + "]", [...s, section, field], component.key, field,
          origin?.attributes[section]?.children?.[field], [...(origin?.attributes[section]?.related ?? []), ...(origin?.related ?? [])]);
    }
    for (const [index, child] of (node.children ?? []).entries()) walk(child, `${jsonPath}.children[${index}]`, [...segments, "children", index]);
  };
  walk(document, "$", []);
  const irText = JSON.stringify(document, null, 2) + "\n";
  const payload = (text: string) => ({ text, sha256: hashText(text) });
  const mapText = JSON.stringify({ version: "1", buildId, irSha256: hashText(irText),
    sources: inputs.filter(f => f.role === "source").map(f => ({ path: f.path, sha256: f.sha256 })), entries });
  const bundle = { kind: "resoloop-build-bundle", bundleVersion: "1", buildId, completion: "committed",
    buildStages: { typecheck: "passed", emit: "passed", evaluate: "passed", inputs: "passed" },
    inputs: { status: "complete", files: inputs }, ir: payload(irText), map: payload(mapText),
    usedTypes: [...types].sort(), catalog: payload(catalogText) };
  if (hashText(catalogText) !== inputs.find(f => f.role === "catalog")!.sha256) throw new Error("inputChanged: catalog snapshot");
  verifyInputs(inputs);
  const temporary = path.join(path.dirname(output), ".bundle-" + randomUUID() + ".tmp");
  try {
    fs.writeFileSync(temporary, JSON.stringify(bundle, null, 2) + "\n", { encoding: "utf8", flag: "wx" });
    verifyInputs(inputs);
    if (fs.existsSync(output)) throw new Error("Bundle output already exists.");
    fs.renameSync(temporary, output);
  } finally {
    if (fs.existsSync(temporary)) fs.rmSync(temporary);
  }
}
