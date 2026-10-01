import * as ts from "typescript";
import * as path from "node:path";
import { hashText } from "./input-snapshot.js";
import type { InputFile } from "./input-snapshot.js";
import { unknown, type Source, type Origin } from "./source-map.js";

// Run before TS's JSX lowering, against the original SourceFile only.
export function sourceTransformer(program: ts.Program, inputs: InputFile[]): ts.CustomTransformers {
  const metadata = new Map<string, { positions: Map<string, any>; helper: ts.Identifier }>();
  const before: ts.TransformerFactory<ts.SourceFile> = context => file => {
    if (file.isDeclarationFile) return file;
    const positions = new Map<string, any>();
    const f = context.factory;
    const helper = f.createUniqueName("__resoloopOrigin");
    metadata.set(file.fileName, { positions, helper });
    const checker = program.getTypeChecker();
    const sha256 = inputs.find(i => i.path === path.resolve(file.fileName))?.sha256 ?? hashText(file.text);
    function source(node: ts.Node): Source {
      const point = (offset: number) => { const p = file.getLineAndCharacterOfPosition(offset); return { offset, line: p.line + 1, column: p.character + 1 }; };
      return { status: "known", file: path.resolve(file.fileName), sha256, range: { start: point(node.getStart(file)), end: point(node.getEnd()) } };
    }
    function forward(node: ts.Expression): string | undefined {
      if (!ts.isPropertyAccessExpression(node) || !ts.isIdentifier(node.expression)) return undefined;
      const declaration = checker.getSymbolAtLocation(node.expression)?.valueDeclaration;
      return declaration && ts.isParameter(declaration) && declaration.parent.parameters[0] === declaration ? node.name.text : undefined;
    }
    function isReferenceCall(node: ts.Expression): boolean {
      if (!ts.isCallExpression(node) || !ts.isPropertyAccessExpression(node.expression)) return false;
      const symbol = checker.getSymbolAtLocation(node.expression.expression);
      const declaration = symbol?.declarations?.[0];
      return !!declaration && ts.isImportSpecifier(declaration) && (declaration.propertyName ?? declaration.name).text === "ref" &&
        ts.isStringLiteral(declaration.parent.parent.parent.moduleSpecifier) && declaration.parent.parent.parent.moduleSpecifier.text === "resoloop-jsx";
    }
    function value(node: ts.Expression): Origin {
      if (ts.isParenthesizedExpression(node) || ts.isAsExpression(node) || ts.isSatisfiesExpression(node)) return value(node.expression);
      const transfer = forward(node);
      const direct = ts.isLiteralExpression(node) || ts.isTemplateExpression(node) || isReferenceCall(node) ||
        node.kind === ts.SyntaxKind.NullKeyword || node.kind === ts.SyntaxKind.TrueKeyword || node.kind === ts.SyntaxKind.FalseKeyword || ts.isPrefixUnaryExpression(node);
      const origin: Origin & { forwardBinding?: string } = { source: source(node), valueSource: direct ? source(node) : unknown, related: [source(node)],
        ...(transfer && ts.isPropertyAccessExpression(node) ? { forward: transfer, forwardBinding: node.expression.getText(file) } : {}) };
      if (ts.isObjectLiteralExpression(node)) {
        origin.children = {};
        // A spread or computed name prevents attribution of the resulting member set.
        if (!node.properties.every(p => ts.isPropertyAssignment(p) && !ts.isComputedPropertyName(p.name))) return origin;
        for (const p of node.properties as ts.NodeArray<ts.PropertyAssignment>) {
          const name = (p.name as ts.Identifier | ts.StringLiteral | ts.NumericLiteral).text;
          origin.children[name] = { ...value(p.initializer), source: source(p.name) };
        }
      } else if (ts.isArrayLiteralExpression(node) && !node.elements.some(ts.isSpreadElement)) {
        origin.children = Object.fromEntries(node.elements.map((e, i) => [String(i), value(e as ts.Expression)]));
      }
      return origin;
    }

    const visit: ts.Visitor = node => {
      if (ts.isJsxElement(node) || ts.isJsxSelfClosingElement(node) || ts.isJsxFragment(node)) {
        const attributes: Record<string, Origin> = {};
        const opening = ts.isJsxElement(node) ? node.openingElement : ts.isJsxSelfClosingElement(node) ? node : undefined;
        const related: Source[] = [];
        for (const attr of opening?.attributes.properties ?? []) {
          if (ts.isJsxSpreadAttribute(attr)) { related.push(source(attr.expression)); continue; }
          const init = attr.initializer;
          const expression = init && ts.isJsxExpression(init) ? init.expression : init;
          attributes[attr.name.getText(file)] = expression ? value(expression as ts.Expression) : { source: source(attr.name), valueSource: source(attr.name) };
        }
        // Spreads can overwrite any explicit attribute as well.
        if (related.length) for (const key of Object.keys(attributes)) attributes[key] = { source: unknown, valueSource: unknown, related: [...related, attributes[key].source] };
        positions.set(`${node.pos}:${node.end}`, { source: source(node), attributes, related });
        return ts.visitEachChild(node, visit, context);
      }
      return ts.visitEachChild(node, visit, context);
    };
    const transformed = ts.visitEachChild(file, visit, context);
    if (!positions.size) return transformed;
    const imported = f.createImportDeclaration(undefined, f.createImportClause(false, undefined, f.createNamespaceImport(helper)),
      f.createStringLiteral("resoloop-jsx/source-runtime"));
    return f.updateSourceFile(transformed, [imported, ...transformed.statements]);
  };
  // TS performs the JSX lowering. Bind metadata to its calls by the original
  // AST node identity range captured above, never by JavaScript source maps.
  const after: ts.TransformerFactory<ts.SourceFile> = context => file => {
    const captured = metadata.get(file.fileName);
    if (!captured?.positions.size) return file;
    const { positions, helper } = captured;
    const f = context.factory;
    function literal(value: any): ts.Expression {
      if (value === undefined) return f.createIdentifier("undefined");
      if (typeof value === "string") return f.createStringLiteral(value);
      if (typeof value === "number") return f.createNumericLiteral(value);
      if (Array.isArray(value)) return f.createArrayLiteralExpression(value.map(literal));
      return f.createObjectLiteralExpression(Object.entries(value).filter(([, v]) => v !== undefined).map(([k, v]) =>
        f.createPropertyAssignment(f.createStringLiteral(k === "forwardBinding" ? "forwardFrom" : k), k === "forwardBinding" ? f.createIdentifier(v as string) : literal(v))));
    }
    const visit: ts.Visitor = node => {
      const original = ts.getOriginalNode(node);
      const origin = ts.isCallExpression(node) ? positions.get(`${original.pos}:${original.end}`) : undefined;
      if (origin && ts.isCallExpression(node)) {
        const args = node.arguments.map(arg => ts.visitNode(arg, visit) as ts.Expression);
        if (args.length === 2) args.push(f.createIdentifier("undefined"));
        return f.updateCallExpression(node, f.createPropertyAccessExpression(helper, "originJsx"), node.typeArguments, [...args, literal(origin)]);
      }
      return ts.visitEachChild(node, visit, context);
    };
    const transformed = ts.visitEachChild(file, visit, context);
    return transformed;
  };
  return { before: [before], after: [after] };
}
