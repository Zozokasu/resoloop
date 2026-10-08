// resoloop-catalog-types: {"formatVersion":"1","contentHash":"7c4931e13cf0983ae63da0f7412b0fed399fffbde029021d545dca4a527789b1","generatorVersion":"3","runtimeFile":"catalog-fields.ts","runtimeSha256":"dfa6c8ee6a0b8e0df5c12bcc48647675ddae44822b53adb35ed5a037d11c4766"}
import "resoloop-jsx";
import type { JsonValue, FieldRef, UncheckedReference } from "resoloop-jsx";
export {};

type CM0 = {"kind":"field","catalogKind":"field","confirmed":true,"memberType":"[FrooxEngine]FrooxEngine.Sync\u003Cstring\u003E","valueType":"string","targetType":null,"scalar":"string","fieldInterface":null,"fieldTarget":null};
type CM1 = {"kind":"field","catalogKind":"field","confirmed":true,"memberType":"[FrooxEngine]FrooxEngine.Sync\u003Cbool\u003E","valueType":"bool","targetType":null,"scalar":"boolean","fieldInterface":null,"fieldTarget":null};
type CM2 = {"kind":"field","catalogKind":"field","confirmed":true,"memberType":"[FrooxEngine]FrooxEngine.Sync\u003Cint\u003E","valueType":"int","targetType":null,"scalar":"int32","fieldInterface":null,"fieldTarget":null};
type CM3 = {"kind":"field","catalogKind":"field","confirmed":true,"memberType":"[FrooxEngine]FrooxEngine.Sync\u003Cfloat\u003E","valueType":"float","targetType":null,"scalar":"single","fieldInterface":null,"fieldTarget":null};

declare module "resoloop-jsx" {
  interface CatalogComponentRegistry {
    "[FrooxEngine]FrooxEngine.AudioDeviceIndexFinder": {
      members: {
        "DeviceName": string;
      };
      memberMetadata: {"DeviceName": CM0;};
      membersComplete: false;
    };
    "[FrooxEngine]FrooxEngine.Light": {
      members: {
        "Enabled": boolean;
        "ShadowMapResolution": number;
      };
      memberMetadata: {"Enabled": CM1; "ShadowMapResolution": CM2;};
      membersComplete: false;
    };
    "[FrooxEngine]FrooxEngine.ValueField\u003Cfloat\u003E": {
      members: {
        "Value": number;
      };
      memberMetadata: {"Value": CM3;};
      membersComplete: false;
    };
    "bool": {
      members: {
      };
      memberMetadata: {};
      membersComplete: false;
    };
    "float": {
      members: {
      };
      memberMetadata: {};
      membersComplete: false;
    };
    "int": {
      members: {
      };
      memberMetadata: {};
      membersComplete: false;
    };
    "string": {
      members: {
      };
      memberMetadata: {};
      membersComplete: false;
    };
  }
}
// resoloop-catalog-fallback: {"type":"[FrooxEngine]FrooxEngine.AudioDeviceIndexFinder","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
// resoloop-catalog-fallback: {"type":"[FrooxEngine]FrooxEngine.Light","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
// resoloop-catalog-fallback: {"type":"[FrooxEngine]FrooxEngine.ValueField\u003Cfloat\u003E","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
// resoloop-catalog-fallback: {"type":"bool","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
// resoloop-catalog-fallback: {"type":"float","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
// resoloop-catalog-fallback: {"type":"int","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
// resoloop-catalog-fallback: {"type":"string","member":null,"reason":"Member names remain open: type or complete member evidence is unavailable."}
