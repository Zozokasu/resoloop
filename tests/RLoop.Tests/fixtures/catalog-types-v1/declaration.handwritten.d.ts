// resoloop-catalog-types: {"formatVersion":"1","contentHash":"91bd5b53e5f843d63c9afa86ac7b96371d88a82ca1fe76260ef9fe39c588e66b","generatorVersion":"1"}
import "resoloop-jsx";
import type { JsonValue } from "resoloop-jsx";
export {};

declare module "resoloop-jsx" {
  interface CatalogComponentRegistry {
    "Alias.Light": {
      members: {
        "Amount": number;
        "Opaque": JsonValue;
        "Target": string | null;
      };
      membersComplete: true;
    };
    "Fixture.Light": {
      members: {
        "Amount": number;
        "Opaque": JsonValue;
        "Target": string | null;
      };
      membersComplete: true;
    };
    "System.Single": {
      members: {
      };
      membersComplete: false;
    };
  }
}
// resoloop-catalog-fallback: {"type":"Alias.Light","member":"Opaque","reason":"Value remains JsonValue: unsupported or unconfirmed member/value evidence or generic closure."}
// resoloop-catalog-fallback: {"type":"Fixture.Light","member":"Opaque","reason":"Value remains JsonValue: unsupported or unconfirmed member/value evidence or generic closure."}
// resoloop-catalog-fallback: {"type":"System.Single","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
