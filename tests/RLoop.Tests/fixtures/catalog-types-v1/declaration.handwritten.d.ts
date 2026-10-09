// resoloop-catalog-types: {"formatVersion":"1","contentHash":"91bd5b53e5f843d63c9afa86ac7b96371d88a82ca1fe76260ef9fe39c588e66b","generatorVersion":"3","runtimeFile":"catalog-fields.ts","runtimeSha256":"f1e9fb8dab561e3e99c4557a7d605b81cdb91e18a94ab2d7aa3eb4aa8c50cda1"}
import "resoloop-jsx";
import type { JsonValue, FieldRef, UncheckedReference } from "resoloop-jsx";
export {};

type CM0 = {"kind":"field","catalogKind":"field","confirmed":true,"memberType":null,"valueType":"System.Single","targetType":null,"scalar":"single","fieldInterface":null,"fieldTarget":null};
type CM1 = {"kind":"field","catalogKind":"field","confirmed":true,"memberType":null,"valueType":"Missing.Value","targetType":null,"scalar":null,"fieldInterface":null,"fieldTarget":null};
type CM2 = {"kind":"reference","catalogKind":"reference","confirmed":true,"memberType":null,"valueType":null,"targetType":"Missing.Target","scalar":null,"fieldInterface":null,"fieldTarget":null};

declare module "resoloop-jsx" {
  interface CatalogComponentRegistry {
    "Alias.Light": {
      members: {
        "Amount": number;
        "Opaque": JsonValue;
        "Target": string | null;
      };
      memberMetadata: {"Amount": CM0; "Opaque": CM1; "Target": CM2;};
      membersComplete: true;
    };
    "Fixture.Light": {
      members: {
        "Amount": number;
        "Opaque": JsonValue;
        "Target": string | null;
      };
      memberMetadata: {"Amount": CM0; "Opaque": CM1; "Target": CM2;};
      membersComplete: true;
    };
    "System.Single": {
      members: {
      };
      memberMetadata: {};
      membersComplete: false;
    };
  }
}
// resoloop-catalog-fallback: {"type":"Alias.Light","member":"Opaque","reason":"Value remains JsonValue: unsupported or unconfirmed member/value evidence or generic closure."}
// resoloop-catalog-fallback: {"type":"Fixture.Light","member":"Opaque","reason":"Value remains JsonValue: unsupported or unconfirmed member/value evidence or generic closure."}
// resoloop-catalog-fallback: {"type":"System.Single","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
