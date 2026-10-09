import * as ts from "typescript";
import * as path from "node:path";
import { hashText } from "./input-snapshot.js";
import type { InputFile } from "./input-snapshot.js";
import { unknown, type Source, type Origin, type ElementOrigin } from "./source-map.js";

interface FileTransform {
  positions: Map<string, ElementOrigin>;
  helper: ts.Identifier;
  instrumented: boolean;
}
interface Forward { name: string; fromProps?: string; functionId?: string }

// Run before TS's JSX lowering, against the original SourceFile only.
export function sourceTransformer(program: ts.Program, inputs: InputFile[]): ts.CustomTransformers {
  const metadata = new Map<string, FileTransform>();
  const before: ts.TransformerFactory<ts.SourceFile> = context => file => {
    if (file.isDeclarationFile) return file;
    const positions = new Map<string, ElementOrigin>();
    const f = context.factory;
    const helper = f.createUniqueName("__resoloopOrigin");
    const state: FileTransform = { positions, helper, instrumented: false };
    metadata.set(file.fileName, state);
    const checker = program.getTypeChecker();
    const sha256 = inputs.find(i => i.path === path.resolve(file.fileName))?.sha256 ?? hashText(file.text);
    function source(node: ts.Node): Source {
      const point = (offset: number) => { const p = file.getLineAndCharacterOfPosition(offset); return { offset, line: p.line + 1, column: p.character + 1 }; };
      return { status: "known", file: path.resolve(file.fileName), sha256,
        range: { start: point(node.getStart(file)), end: point(node.getEnd()) } };
    }
    function identity(fn: ts.FunctionLikeDeclaration): string {
      return `${sha256}:${fn.getStart(file)}:${fn.getEnd()}`;
    }
    function executableFunction(node: ts.SignatureDeclaration): node is ts.FunctionLikeDeclaration {
      return ts.isFunctionDeclaration(node) || ts.isMethodDeclaration(node) || ts.isGetAccessorDeclaration(node) ||
        ts.isSetAccessorDeclaration(node) || ts.isConstructorDeclaration(node) || ts.isFunctionExpression(node) || ts.isArrowFunction(node);
    }
    function assignmentOperator(kind: ts.SyntaxKind): boolean {
      return kind === ts.SyntaxKind.EqualsToken ||
        kind >= ts.SyntaxKind.FirstCompoundAssignment && kind <= ts.SyntaxKind.LastCompoundAssignment;
    }
    function transparentExpressionWrapper(node: ts.Node): node is ts.ParenthesizedExpression | ts.AsExpression |
      ts.SatisfiesExpression | ts.NonNullExpression | ts.TypeAssertion {
      return ts.isParenthesizedExpression(node) || ts.isAsExpression(node) || ts.isSatisfiesExpression(node) ||
        ts.isNonNullExpression(node) || ts.isTypeAssertionExpression(node);
    }
    function unwrapExpression(node: ts.Expression): ts.Expression {
      while (transparentExpressionWrapper(node)) node = node.expression;
      return node;
    }
    function isWrite(node: ts.Node): boolean {
      let target = node;
      while (true) {
        const parent = target.parent;
        if (ts.isBinaryExpression(parent) && parent.left === target && assignmentOperator(parent.operatorToken.kind)) return true;
        if ((ts.isForInStatement(parent) || ts.isForOfStatement(parent)) && parent.initializer === target) return true;
        if (ts.isDeleteExpression(parent) && parent.expression === target ||
            ts.isPrefixUnaryExpression(parent) && parent.operand === target &&
              (parent.operator === ts.SyntaxKind.PlusPlusToken || parent.operator === ts.SyntaxKind.MinusMinusToken) ||
            ts.isPostfixUnaryExpression(parent) && parent.operand === target) return true;
        if (transparentExpressionWrapper(parent) && parent.expression === target) {
          target = parent;
          continue;
        }
        if (ts.isParenthesizedExpression(parent) && parent.expression === target ||
            ts.isObjectLiteralExpression(parent) && parent.properties.includes(target as ts.ObjectLiteralElementLike) ||
            ts.isArrayLiteralExpression(parent) && parent.elements.includes(target as ts.Expression) ||
            ts.isPropertyAssignment(parent) && parent.initializer === target ||
            ts.isShorthandPropertyAssignment(parent) && parent.name === target ||
            ts.isSpreadAssignment(parent) && parent.expression === target ||
            ts.isSpreadElement(parent) && parent.expression === target) {
          target = parent;
          continue;
        }
        return false;
      }
    }
    function bindingWritten(fn: ts.FunctionLikeDeclaration, symbol: ts.Symbol): boolean {
      let written = false;
      const scan = (node: ts.Node): void => {
        const nodeSymbol = ts.isIdentifier(node) && ts.isShorthandPropertyAssignment(node.parent) && node.parent.name === node
          ? checker.getShorthandAssignmentValueSymbol(node.parent) : checker.getSymbolAtLocation(node);
        if (ts.isIdentifier(node) && nodeSymbol === symbol && isWrite(node)) written = true;
        if (!written) ts.forEachChild(node, scan);
      };
      for (const parameter of fn.parameters) scan(parameter);
      if (fn.body) scan(fn.body);
      return written;
    }
    function propertyWritten(fn: ts.FunctionLikeDeclaration, parameter: ts.ParameterDeclaration, name: string): boolean {
      const symbol = checker.getSymbolAtLocation(parameter.name);
      if (!symbol) return true;
      let written = false;
      const scan = (node: ts.Node): void => {
        if (ts.isIdentifier(node) && checker.getSymbolAtLocation(node) === symbol && isWrite(node)) written = true;
        if (ts.isPropertyAccessExpression(node) && ts.isIdentifier(unwrapExpression(node.expression)) && node.name.text === name &&
            checker.getSymbolAtLocation(unwrapExpression(node.expression)) === symbol && isWrite(node)) written = true;
        if (ts.isElementAccessExpression(node) && ts.isIdentifier(unwrapExpression(node.expression)) &&
            checker.getSymbolAtLocation(unwrapExpression(node.expression)) === symbol && isWrite(node)) written = true;
        if (!written) ts.forEachChild(node, scan);
      };
      for (const parameterValue of fn.parameters) scan(parameterValue);
      if (fn.body) scan(fn.body);
      return written;
    }
    function transfer(node: ts.Expression): Forward | undefined {
      if (ts.isPropertyAccessExpression(node) && ts.isIdentifier(node.expression)) {
        const declaration = checker.getSymbolAtLocation(node.expression)?.valueDeclaration;
        if (declaration && ts.isParameter(declaration)) {
          const parent = declaration.parent;
          if (executableFunction(parent) && parent.parameters[0] === declaration && ts.isIdentifier(declaration.name) &&
              !propertyWritten(parent, declaration, node.name.text))
            return { name: node.name.text, fromProps: node.expression.getText(file) };
        }
        return undefined;
      }
      if (!ts.isIdentifier(node)) return undefined;
      const declaration = checker.getSymbolAtLocation(node)?.valueDeclaration;
      if (!declaration || !ts.isBindingElement(declaration) || declaration.dotDotDotToken ||
          !ts.isIdentifier(declaration.name) || !ts.isObjectBindingPattern(declaration.parent) ||
          !ts.isParameter(declaration.parent.parent)) return undefined;
      const parameter = declaration.parent.parent;
      const fn = parameter.parent;
      if (!executableFunction(fn) || fn.parameters[0] !== parameter || !declaration.propertyName && !ts.isIdentifier(declaration.name) ||
          declaration.propertyName && !ts.isIdentifier(declaration.propertyName) && !ts.isStringLiteral(declaration.propertyName)) return undefined;
      const name = declaration.propertyName ? declaration.propertyName.text : declaration.name.text;
      const bindingSymbol = checker.getSymbolAtLocation(declaration.name);
      if (!bindingSymbol || bindingWritten(fn, bindingSymbol)) return undefined;
      return { name, functionId: identity(fn) };
    }
    function isImported(node: ts.Node, name: string, modules: readonly string[]): boolean {
      const symbol = checker.getSymbolAtLocation(node);
      for (const declaration of symbol?.declarations ?? []) {
        if (!ts.isImportSpecifier(declaration)) continue;
        const importDeclaration = declaration.parent.parent.parent;
        if (ts.isImportDeclaration(importDeclaration) && ts.isStringLiteral(importDeclaration.moduleSpecifier) &&
            modules.includes(importDeclaration.moduleSpecifier.text) &&
            (declaration.propertyName ?? declaration.name).text === name) return true;
      }
      return false;
    }
    function referenceCall(node: ts.Expression): boolean {
      if (!ts.isCallExpression(node) || !ts.isPropertyAccessExpression(node.expression)) return false;
      return node.expression.name.text !== "" && isImported(node.expression.expression, "ref", ["resoloop-jsx", "resoloop-jsx/draft"]);
    }
    function definitionMetadata(node: ts.CallExpression): ElementOrigin | undefined {
      if (node.arguments.length !== 1 || !ts.isObjectLiteralExpression(node.arguments[0]) ||
          !ts.isCallExpression(node.expression) || node.expression.arguments.length !== 0 ||
          !ts.isIdentifier(node.expression.expression) ||
          !isImported(node.expression.expression, "defineObject", ["resoloop-jsx", "resoloop-jsx/draft"])) return undefined;
      const definition = node.arguments[0];
      const attributes: Record<string, Origin> = {};
      let bodyOrigin: Origin | undefined;
      let bodyExportOrigin: Origin | undefined;
      let uncertainBodyExport = false;
      for (const property of definition.properties) {
        if (!ts.isPropertyAssignment(property) || !ts.isIdentifier(property.name) && !ts.isStringLiteral(property.name)) continue;
        const name = property.name.text;
        if (name === "body") {
          const bodySource = source(property.initializer);
          bodyOrigin = { source: bodySource, valueSource: bodySource, related: [bodySource] };
        }
        if (name !== "exports") continue;
        if (!ts.isObjectLiteralExpression(property.initializer)) { uncertainBodyExport = true; continue; }
        const uncertainExports = property.initializer.properties.some(exported =>
          !ts.isPropertyAssignment(exported) || ts.isComputedPropertyName(exported.name) ||
          !ts.isIdentifier(exported.name) && !ts.isStringLiteral(exported.name));
        uncertainBodyExport ||= uncertainExports;
        for (const exported of property.initializer.properties) {
          if (!ts.isPropertyAssignment(exported) || ts.isComputedPropertyName(exported.name) ||
              !ts.isIdentifier(exported.name) && !ts.isStringLiteral(exported.name)) continue;
          if (uncertainExports) {
            if (exported.name.text === "body") bodyExportOrigin = { source: unknown, valueSource: unknown };
            else attributes[exported.name.text] = { source: unknown, valueSource: unknown };
            continue;
          }
          const valueSource = source(exported.initializer);
          if (exported.name.text === "body") bodyExportOrigin = { source: valueSource, valueSource, related: [valueSource] };
          else attributes[exported.name.text] = { source: valueSource, valueSource, related: [valueSource] };
        }
      }
      if (bodyOrigin && bodyExportOrigin) attributes.body = { ...bodyOrigin,
        valueSource: bodyExportOrigin.valueSource,
        related: [...(bodyOrigin.related ?? []), ...(bodyExportOrigin.related ?? [])] };
      else if (bodyOrigin) attributes.body = uncertainBodyExport
        ? { ...bodyOrigin, valueSource: unknown } : bodyOrigin;
      else if (bodyExportOrigin) attributes.body = bodyExportOrigin;
      const definitionSource = source(node);
      return { source: definitionSource, attributes };
    }
    function value(node: ts.Expression): Origin {
      if (ts.isParenthesizedExpression(node) || ts.isAsExpression(node) || ts.isSatisfiesExpression(node)) return value(node.expression);
      const forwarded = transfer(node);
      const direct = ts.isLiteralExpression(node) || referenceCall(node) ||
        node.kind === ts.SyntaxKind.NullKeyword || node.kind === ts.SyntaxKind.TrueKeyword || node.kind === ts.SyntaxKind.FalseKeyword ||
        ts.isPrefixUnaryExpression(node) && (node.operator === ts.SyntaxKind.PlusToken || node.operator === ts.SyntaxKind.MinusToken) &&
          ts.isNumericLiteral(node.operand);
      const origin: Origin & { forwardBinding?: string } = { source: source(node), valueSource: direct ? source(node) : unknown, related: [source(node)],
        ...(forwarded ? { forward: forwarded.name, forwardBinding: forwarded.fromProps,
          forwardFunctionId: forwarded.functionId } : {}) };
      if (ts.isObjectLiteralExpression(node)) {
        origin.children = {};
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
    function registration(name: ts.Identifier, id: string): ts.Statement {
      state.instrumented = true;
      return f.createExpressionStatement(f.createCallExpression(f.createPropertyAccessExpression(helper, "sourceFunction"), undefined,
        [f.createIdentifier(name.text), f.createStringLiteral(id)]));
    }
    function staticFunctionName(node: ts.FunctionExpression | ts.ArrowFunction): string | undefined {
      let expression: ts.Expression = node;
      while (ts.isParenthesizedExpression(expression.parent) || ts.isAsExpression(expression.parent) || ts.isSatisfiesExpression(expression.parent))
        expression = expression.parent;
      const parent = expression.parent;
      if (ts.isVariableDeclaration(parent) && parent.initializer === expression && ts.isIdentifier(parent.name)) return parent.name.text;
      if (ts.isParameter(parent) && parent.initializer === expression && ts.isIdentifier(parent.name)) return parent.name.text;
      if (ts.isBindingElement(parent) && parent.initializer === expression && ts.isIdentifier(parent.name)) return parent.name.text;
      if (ts.isPropertyDeclaration(parent) && parent.initializer === expression) {
        if (ts.isPrivateIdentifier(parent.name)) return parent.name.text;
        if (ts.isIdentifier(parent.name) || ts.isStringLiteral(parent.name) || ts.isNumericLiteral(parent.name)) return parent.name.text;
        if (ts.isComputedPropertyName(parent.name)) {
          const key = parent.name.expression;
          if (ts.isStringLiteral(key) || ts.isNoSubstitutionTemplateLiteral(key)) return key.text;
          if (ts.isNumericLiteral(key)) return String(Number(key.text.replaceAll("_", "")));
        }
      }
      if (ts.isExportAssignment(parent) && parent.expression === expression) return "default";
      if (ts.isPropertyAssignment(parent) && parent.initializer === expression) {
        if (ts.isComputedPropertyName(parent.name)) {
          const key = parent.name.expression;
          return ts.isStringLiteral(key) || ts.isNoSubstitutionTemplateLiteral(key) || ts.isNumericLiteral(key) ? key.text : undefined;
        }
        if (ts.isIdentifier(parent.name) || ts.isStringLiteral(parent.name) || ts.isNumericLiteral(parent.name)) {
          if (parent.name.text === "__proto__" && !ts.isNumericLiteral(parent.name)) return undefined;
          return parent.name.text;
        }
      }
      if (ts.isBinaryExpression(parent) && parent.right === expression && ts.isIdentifier(parent.left) &&
          (parent.operatorToken.kind === ts.SyntaxKind.EqualsToken || parent.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandEqualsToken ||
           parent.operatorToken.kind === ts.SyntaxKind.BarBarEqualsToken || parent.operatorToken.kind === ts.SyntaxKind.QuestionQuestionEqualsToken))
        return parent.left.text;
      return undefined;
    }
    function dynamicComputedName(node: ts.FunctionExpression | ts.ArrowFunction): boolean {
      let expression: ts.Expression = node;
      while (ts.isParenthesizedExpression(expression.parent) || ts.isAsExpression(expression.parent) || ts.isSatisfiesExpression(expression.parent))
        expression = expression.parent;
      const parent = expression.parent;
      if (ts.isPropertyDeclaration(parent) && parent.initializer === expression && ts.isComputedPropertyName(parent.name)) {
        const key = parent.name.expression;
        return !(ts.isStringLiteral(key) || ts.isNoSubstitutionTemplateLiteral(key) || ts.isNumericLiteral(key));
      }
      if (ts.isPropertyAssignment(parent) && parent.initializer === expression && ts.isComputedPropertyName(parent.name)) {
        const key = parent.name.expression;
        return !(ts.isStringLiteral(key) || ts.isNoSubstitutionTemplateLiteral(key) || ts.isNumericLiteral(key));
      }
      if (ts.isBinaryExpression(parent) && parent.right === expression && ts.isElementAccessExpression(parent.left)) {
        const key = parent.left.argumentExpression;
        return !!key && !(ts.isStringLiteral(key) || ts.isNoSubstitutionTemplateLiteral(key) || ts.isNumericLiteral(key));
      }
      return false;
    }
    function sourceFunctionValue(node: ts.FunctionExpression | ts.ArrowFunction, visited: ts.Expression, id: string): ts.Expression {
      if (!node.name && dynamicComputedName(node)) return visited;
      const name = node.name?.text ?? staticFunctionName(node);
      let value = visited;
      if (name !== undefined) {
        const property = f.createPropertyAssignment(f.createComputedPropertyName(f.createStringLiteral(name)), visited);
        value = f.createElementAccessExpression(f.createParenthesizedExpression(f.createObjectLiteralExpression([property])),
          f.createStringLiteral(name));
      }
      state.instrumented = true;
      return f.createCallExpression(f.createPropertyAccessExpression(helper, "sourceFunction"), undefined,
        [value, f.createStringLiteral(id)]);
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
        if (related.length) for (const key of Object.keys(attributes))
          attributes[key] = { source: unknown, valueSource: unknown, related: [...related, attributes[key].source] };
        positions.set(`${node.pos}:${node.end}`, { source: source(node), attributes, related });
        return ts.visitEachChild(node, visit, context);
      }
      if (ts.isCallExpression(node)) {
        const definition = definitionMetadata(node);
        if (definition) {
          state.instrumented = true;
          const call = ts.visitEachChild(node, visit, context) as ts.CallExpression;
          const metadataLiteral = literal(definition, f);
          return f.createCallExpression(f.createPropertyAccessExpression(helper, "sourceDefinitionValue"), undefined,
            [call, metadataLiteral]);
        }
      }
      if (ts.isBlock(node)) return f.updateBlock(node, statements(node.statements));
      if (ts.isArrowFunction(node) || ts.isFunctionExpression(node)) {
        const first = node.parameters[0];
        if (first && ts.isObjectBindingPattern(first.name)) {
          const id = identity(node);
          const visited = ts.visitEachChild(node, visit, context) as typeof node;
          return sourceFunctionValue(node, visited, id);
        }
      }
      return ts.visitEachChild(node, visit, context);
    };
    function statements(input: readonly ts.Statement[]): ts.NodeArray<ts.Statement> {
      const output: ts.Statement[] = [];
      for (const statement of input) {
        const visited = ts.visitNode(statement, visit) as ts.Statement;
        if (ts.isFunctionDeclaration(statement) && statement.name && statement.parameters[0] &&
            ts.isObjectBindingPattern(statement.parameters[0].name))
          output.push(registration(statement.name, identity(statement)));
        output.push(visited);
      }
      return f.createNodeArray(output);
    }
    function transformLists(node: ts.Node): ts.Node {
      if (ts.isSourceFile(node)) return f.updateSourceFile(node, statements(node.statements));
      if (ts.isBlock(node)) return f.updateBlock(node, statements(node.statements));
      return ts.visitEachChild(node, visit, context);
    }
    const transformed = transformLists(file) as ts.SourceFile;
    // JSX, function identity, and definitions share the existing private runtime subpath.
    if (positions.size) state.instrumented = true;
    if (!state.instrumented) return transformed;
    const imported = f.createImportDeclaration(undefined, f.createImportClause(false, undefined, f.createNamespaceImport(helper)),
      f.createStringLiteral("resoloop-jsx/source-runtime"));
    return f.updateSourceFile(transformed, [imported, ...transformed.statements]);
  };
  // TS performs the JSX lowering. Bind metadata to its calls by original-node identity.
  const after: ts.TransformerFactory<ts.SourceFile> = context => file => {
    const captured = metadata.get(file.fileName);
    if (!captured?.positions.size) return file;
    const { positions, helper } = captured;
    const f = context.factory;
    function literal(value: unknown, factory: ts.NodeFactory): ts.Expression {
      if (value === undefined) return factory.createIdentifier("undefined");
      if (typeof value === "string") return factory.createStringLiteral(value);
      if (typeof value === "number") return factory.createNumericLiteral(value);
      if (Array.isArray(value)) return factory.createArrayLiteralExpression(value.map(item => literal(item, factory)));
      return factory.createObjectLiteralExpression(Object.entries(value as Record<string, unknown>).filter(([, v]) => v !== undefined).map(([k, v]) =>
        factory.createPropertyAssignment(factory.createStringLiteral(k === "forwardBinding" ? "forwardFrom" : k),
          k === "forwardBinding" ? factory.createIdentifier(v as string) : literal(v, factory))));
    }
    const visit: ts.Visitor = node => {
      const original = ts.getOriginalNode(node);
      const origin = ts.isCallExpression(node) ? positions.get(`${original.pos}:${original.end}`) : undefined;
      if (origin && ts.isCallExpression(node)) {
        const args = node.arguments.map(arg => ts.visitNode(arg, visit) as ts.Expression);
        if (args.length === 2) args.push(f.createIdentifier("undefined"));
        return f.updateCallExpression(node, f.createPropertyAccessExpression(helper, "originJsx"), node.typeArguments,
          [...args, literal(origin, f)]);
      }
      return ts.visitEachChild(node, visit, context);
    };
    return ts.visitEachChild(file, visit, context);
  };
  return { before: [before], after: [after] };
}

function literal(value: unknown, factory: ts.NodeFactory): ts.Expression {
  if (value === undefined) return factory.createIdentifier("undefined");
  if (typeof value === "string") return factory.createStringLiteral(value);
  if (typeof value === "number") return factory.createNumericLiteral(value);
  if (Array.isArray(value)) return factory.createArrayLiteralExpression(value.map(item => literal(item, factory)));
  const entries = Object.entries(value as Record<string, unknown>);
  return factory.createObjectLiteralExpression(entries.filter(([, child]) => child !== undefined).map(([key, child]) =>
    factory.createPropertyAssignment(factory.createStringLiteral(key), literal(child, factory))));
}
