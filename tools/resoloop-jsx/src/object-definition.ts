import { callComponent, captureElement, copyOrigin } from "./source-map.js";
import { createHandle, newDefinitionIdentity, type InstanceHandle, type PublicRefs } from "./object-refs.js";
import type { JsxNode, ObjectElement } from "./elements.js";

/** Internal descriptor carried by ObjectElement until object collection. */
export interface ObjectDescriptor {
  readonly definitionId: string;
  readonly exports: PublicRefs;
  readonly body: (props: any) => JsxNode;
}

/** @experimental A reusable Object definition with definition-owned handles. */
export interface ObjectDefinition<P, E extends PublicRefs> {
  (props: P & { handle: InstanceHandle<E> }): JsxNode;
  instance(id: string): InstanceHandle<E>;
}

/** @experimental Define an Object body and its typed exported references. */
export function defineObject<P>(): <const E extends PublicRefs>(
  definition: Extract<keyof P, "handle"> extends never
    ? { readonly exports: E; readonly body: (props: P & { handle: InstanceHandle<E> }) => JsxNode }
    : never
) => ObjectDefinition<P, E> {
  return function<const E extends PublicRefs>(
    input: Extract<keyof P, "handle"> extends never
      ? { readonly exports: E; readonly body: (props: P & { handle: InstanceHandle<E> }) => JsxNode }
      : never
  ): ObjectDefinition<P, E> {
    const definition = input as unknown as {
      readonly exports: E;
      readonly body: (props: P & { handle: InstanceHandle<E> }) => JsxNode;
    };
    const descriptor: ObjectDescriptor = Object.freeze({
      definitionId: newDefinitionIdentity(),
      exports: Object.freeze({ ...definition.exports }),
      body: definition.body,
    });
    const ObjectComponent = (props: P & { handle: InstanceHandle<E> }): JsxNode => {
      copyOrigin(ObjectComponent, descriptor);
      const element: ObjectElement = {
        kind: "object",
        props: {
          handle: props.handle,
          body: callComponent(descriptor.body, props),
          definition: descriptor,
        },
      };
      captureElement(element);
      return element;
    };
    const callable = Object.assign(ObjectComponent, {
      instance(id: string): InstanceHandle<E> {
        return createHandle(id, descriptor.exports as E, descriptor.definitionId);
      },
    });
    Object.freeze(callable);
    copyOrigin(callable, descriptor);
    return callable as ObjectDefinition<P, E>;
  };
}
