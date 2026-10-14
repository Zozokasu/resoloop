import { test } from "node:test";
import { equal } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const SEED = 20261013;
const CASES = 200;
const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const names = ["Lamp", "Lamp", "", "lamp", "Lamp!", "Lamp?"];
const chdir = (directory: string): void =>
  (process as unknown as { chdir(path: string): void }).chdir(directory);

function mulberry32(seed: number): () => number {
  return () => {
    let value = seed += 0x6D2B79F5;
    value = Math.imul(value ^ value >>> 15, value | 1);
    value ^= value + Math.imul(value ^ value >>> 7, value | 61);
    return ((value ^ value >>> 14) >>> 0) / 4294967296;
  };
}

interface SlotNode { name: string; key?: string; children: SlotNode[]; components: (string | undefined)[] }
function generate(random: () => number, uniqueNames = false): { root: SlotNode; emptyName: boolean } {
  let serial = 0, emptyName = random() < 0.5;
  const slot = (depth: number): SlotNode => {
    const nodeName = names[Math.floor(random() * names.length)];
    const node: SlotNode = {
      name: nodeName || "Lamp",
      ...(random() < 0.5 ? { key: `key-${serial}` } : {}),
      children: [],
      components: Array.from({ length: Math.floor(random() * 3) }, () => random() < 0.5 ? `component-${serial++}` : undefined),
    };
    serial++;
    if (depth > 1) {
      const count = Math.floor(random() * 6);
      const used = new Set<string>();
      for (let i = 0; i < count; i++) {
        let child = slot(depth - 1);
        if (uniqueNames) {
          let attempts = 0;
          while (used.has(child.name) && attempts++ < 12) child = slot(depth - 1);
          if (used.has(child.name)) child.name = `Unique${i}`;
          used.add(child.name);
        }
        node.children.push(child);
      }
    }
    return node;
  };
  const depth = 1 + Math.floor(random() * 4);
  const root = slot(depth);
  if (uniqueNames) root.name = "Root";
  return { root, emptyName };
}

function render(node: SlotNode, keyed = false, removedKey = -1, includeEmptyName = false): string {
  let index = 0;
  const renderSlot = (slot: SlotNode): string => {
    const key = keyed ? `key="property-${index}"` : slot.key === undefined ? "" : `key="${slot.key}"`;
    if (keyed) index++;
    const components = slot.components.map((value) => {
      const componentKey = keyed ? `key="property-${index}"` : value === undefined ? "" : `key="${value}"`;
      if (keyed) index++;
      return `<Component ${componentKey} type="Probe" />`;
    });
    const children = [...slot.children.map(renderSlot), ...components];
    return `<Slot ${key} name=${JSON.stringify(slot.name)}>${children.join("")}</Slot>`;
  };
  let jsx = renderSlot(node);
  if (removedKey >= 0) {
    let count = 0;
    jsx = jsx.replace(/key="property-\d+"/g, (value) => count++ === removedKey ? "" : value);
  }
  return `import { Slot, Component } from "resoloop-jsx";\nexport const ownership = { key: "entry" };\nexport default (<>${jsx}${includeEmptyName ? '{false && <Slot name="" />}' : ""}</>);\n`;
}

function collectKeys(document: any): string[] {
  const result: string[] = [];
  const visit = (container: any): void => {
    if (typeof container?.slot?.key === "string") result.push(container.slot.key);
    for (const component of container?.components ?? []) if (typeof component.key === "string") result.push(component.key);
    for (const child of container?.children ?? []) visit(child);
  };
  visit(document);
  return result;
}

test("seeded trees preserve entry aliases, cwd independence, unique creation keys, and required ordinary keys", async () => {
  const random = mulberry32(SEED);
  const work = fs.mkdtempSync(path.join(packageRoot, ".one-entry-property-"));
  const entry = path.join(work, "main.tsx"), output = path.join(work, "out.json");
  const cwd = process.cwd();
  const run = async (options: { target?: "creation" } = {}) => buildFile(entry, { ...options, output, projectRoot: work });
  const check = (condition: unknown, message: string, caseNo: number, tsx: string): void => {
    if (!condition) throw new Error(`${message}; seed=${SEED}; case=${caseNo}; TSX:\n${tsx}`);
  };
  try {
    for (let caseNo = 0; caseNo < CASES; caseNo++) {
      const generated = generate(random);
      const tsx = render(generated.root, false, -1, generated.emptyName);
      try {
        fs.writeFileSync(entry, tsx);
        const main = await run({ target: "creation" });
        check(main.exitCode === 0, `P01 main creation failed: ${main.errors.join("\n")}`, caseNo, tsx);
        const mainBytes = fs.readFileSync(output, { encoding: "utf8" });

        fs.writeFileSync(entry, tsx.replace('from "resoloop-jsx"', 'from "resoloop-jsx/draft"'));
        const draft = await run({ target: "creation" });
        check(draft.exitCode === 0, `P01 draft creation failed: ${draft.errors.join("\n")}`, caseNo, tsx);
        equal(fs.readFileSync(output, { encoding: "utf8" }), mainBytes);

        fs.writeFileSync(entry, tsx);
        let fromA, fromB, bytesA = "", bytesB = "";
        try {
          chdir(packageRoot);
          fromA = await run({ target: "creation" });
          bytesA = fs.readFileSync(output, { encoding: "utf8" });
          chdir(work);
          fromB = await run({ target: "creation" });
          bytesB = fs.readFileSync(output, { encoding: "utf8" });
        } finally { chdir(cwd); }
        check(fromA?.exitCode === 0 && fromB?.exitCode === 0,
          `P02 cwd build failed: ${fromA?.errors.join("\n")} ${fromB?.errors.join("\n")}`, caseNo, tsx);
        equal(bytesA, bytesB);

        const keys = collectKeys(main.creation?.definition);
        equal(new Set(keys).size, keys.length, `P03 duplicate keys; seed=${SEED}; case=${caseNo}; TSX:\n${tsx}`);

        const uniqueTree = generate(random, true).root;
        const keyed = render(uniqueTree, true);
        fs.writeFileSync(entry, keyed);
        const complete = await run();
        check(complete.exitCode === 0, `P04 keyed tree failed: ${complete.errors.join("\n")}`, caseNo, keyed);
        const count = (keyed.match(/key="property-\d+"/g) ?? []).length;
        const missing = render(uniqueTree, true, Math.floor(random() * count));
        fs.rmSync(output, { force: true });
        fs.writeFileSync(entry, missing);
        const incomplete = await run();
        check(incomplete.exitCode === 1 && incomplete.errors.some((message) => message.includes("EXPLICIT_KEY_REQUIRED")),
          `P04 missing key was not rejected as EXPLICIT_KEY_REQUIRED: ${incomplete.errors.join("\n")}`, caseNo, missing);
        check(!fs.existsSync(output), "P04 wrote output after missing key", caseNo, missing);
      } catch (error) {
        if (error instanceof Error && error.message.includes(`seed=${SEED}; case=${caseNo}`)) throw error;
        throw new Error(`seed=${SEED}; case=${caseNo}; TSX:\n${tsx}\n${error instanceof Error ? error.stack ?? error.message : String(error)}`);
      }
    }

    const many = `import { Slot } from "resoloop-jsx";\nexport const ownership = { key: "entry" };\nexport default <Slot name="Root">${Array.from({ length: 50 }, () => '<Slot name="Same" />').join("")}</Slot>;\n`;
    fs.writeFileSync(entry, many);
    const fifty = await run({ target: "creation" });
    equal(fifty.exitCode, 0, `P03 50 siblings failed: ${fifty.errors.join("\n")}`);
    const manyKeys = collectKeys(fifty.creation?.definition);
    equal(manyKeys.length, 51);
    equal(new Set(manyKeys).size, manyKeys.length);
  } finally {
    chdir(cwd);
    fs.rmSync(work, { recursive: true, force: true });
  }
});
