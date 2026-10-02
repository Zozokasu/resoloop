// resoloop-catalog-types: {"formatVersion":"1","contentHash":"handwritten-catalog-hash","generatorVersion":"1"}
import "resoloop-jsx";
import type { JsonValue } from "resoloop-jsx";
export {};
declare module "resoloop-jsx" {
  interface CatalogComponentRegistry {
    "Synthetic.Strict": {
      members: {
        Amount: number;
        Enabled: JsonValue;
        Caption: number | null;
        Position: [number, number, number] | { x: number; y: number; z: number } | { r: number; g: number; b: number };
        Mode: "Fast" | "Slow" | number;
        Target: string | null;
        Unsupported: JsonValue;
      };
      membersComplete: true;
    };
    "Synthetic.Open": {
      members: { Amount: number };
      membersComplete: false;
    };
    "Synthetic.Empty": { members: {}; membersComplete: true };
  }
}
