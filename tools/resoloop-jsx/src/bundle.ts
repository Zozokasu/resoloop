import * as fs from "node:fs";
import * as path from "node:path";
import { randomUUID } from "node:crypto";
import type { ApplyDocument } from "./evaluate.js";
import { hashText, verifyInputs, type InputFile } from "./input-snapshot.js";

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
  const entries: { jsonPath: string; source: { status: "unknown" } }[] = [];
  const unknown = (jsonPath: string): void => { entries.push({ jsonPath, source: { status: "unknown" } }); };
  const walk = (node: any, jsonPath: string): void => {
    unknown(jsonPath + ".slot");
    for (const property of Object.keys(node.slot ?? {}).sort()) unknown(jsonPath + ".slot[" + JSON.stringify(property) + "]");
    for (const [index, component] of (node.components ?? []).entries()) {
      const p = `${jsonPath}.components[${index}]`;
      const full = catalog.content?.aliases?.[component.type] ?? component.type;
      // Identification only. Core is the sole semantic/evidence validator.
      types.add(catalog.content?.types?.find((t: any) => t.fullName === full && t.confirmed)?.fullName ?? component.type);
      unknown(p); unknown(p + ".type");
      for (const section of ["fields", "initialFields"])
        for (const field of Object.keys(component[section] ?? {}).sort()) unknown(p + "." + section + "[" + JSON.stringify(field) + "]");
    }
    for (const [index, child] of (node.children ?? []).entries()) walk(child, `${jsonPath}.children[${index}]`);
  };
  walk(document, "$");
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
