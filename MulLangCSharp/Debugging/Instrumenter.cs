using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MulLangCSharp.Build;
using MulLangCSharp.Language;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace MulLangCSharp.Debugging;

/// <summary>
/// 偵錯插樁：在每一個陳述式前插入 Rt.H(行號, 變數名稱, 變數值)，
/// 並以 Rt.Enter/Rt.Exit 包住函式本體以追蹤呼叫堆疊。
/// 行號取自插樁前的語法樹，因此與中文原始碼的行號一致。
/// </summary>
public static class Instrumenter
{
    private const string Rt = "global::__MulLangDbg.Rt";
    private static readonly SyntaxAnnotation HookAnnotation = new("MulLangHook");

    public static CSharpCompilation? Instrument(CSharpCompilation compilation, SyntaxTree tree, List<string> notes)
    {
        try
        {
            var model = compilation.GetSemanticModel(tree);
            var newRoot = new HookRewriter(model).Visit(tree.GetRoot());
            var newTree = CSharpSyntaxTree.Create((CSharpSyntaxNode)newRoot, CodeCompiler.ParseOptions, tree.FilePath, Encoding.UTF8);
            var rtTree = CSharpSyntaxTree.ParseText(DebugRuntimeSource.Code, CodeCompiler.ParseOptions, "__MulLangDbg.cs", Encoding.UTF8);
            var comp = compilation.ReplaceSyntaxTree(tree, newTree).AddSyntaxTrees(rtTree);

            // 若某些掛鉤擷取變數造成編譯錯誤（例如 ref 參數被 lambda 擷取），改為不擷取變數再試。
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (errors.Count == 0)
                {
                    if (Environment.GetEnvironmentVariable("MULLANG_DUMP_INSTRUMENTED") is { Length: > 0 } dump)
                        System.IO.File.WriteAllText(dump, newTree.ToString());
                    return comp;
                }

                var root = newTree.GetRoot();
                var bad = errors
                    .Where(e => e.Location.SourceTree == newTree)
                    .Select(e => root.FindNode(e.Location.SourceSpan, getInnermostNodeForTie: true)
                                     .AncestorsAndSelf().FirstOrDefault(n => n.HasAnnotation(HookAnnotation)))
                    .OfType<StatementSyntax>()
                    .Distinct()
                    .ToList();
                if (bad.Count == 0 || errors.Any(e => e.Location.SourceTree != newTree))
                {
                    foreach (var e in errors.Take(5)) notes.Add("插樁錯誤：" + e);
                    return null;
                }

                var fixedRoot = root.ReplaceNodes(bad, (orig, _) => StripVariables(orig));
                var fixedTree = CSharpSyntaxTree.Create((CSharpSyntaxNode)fixedRoot, CodeCompiler.ParseOptions, tree.FilePath, Encoding.UTF8);
                comp = comp.ReplaceSyntaxTree(newTree, fixedTree);
                newTree = fixedTree;
            }
            return null;
        }
        catch (Exception ex)
        {
            notes.Add("插樁例外：" + ex.Message);
            return null;
        }
    }

    private static StatementSyntax StripVariables(StatementSyntax hook)
    {
        var call = (InvocationExpressionSyntax)((ExpressionStatementSyntax)hook).Expression;
        var line = call.ArgumentList.Arguments[0].ToString();
        return ParseStatement($"{Rt}.H({line}, null, null);").WithAdditionalAnnotations(HookAnnotation);
    }

    private sealed class HookRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;

        public HookRewriter(SemanticModel model) => _model = model;

        // ── 區塊與 switch 區段：在每個陳述式前插入掛鉤 ──

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            visited = visited.WithStatements(Interleave(node.Statements, visited.Statements));
            return IsFunctionBody(node) ? WrapFrame(node, visited) : visited;
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
            return visited.WithStatements(Interleave(node.Statements, visited.Statements));
        }

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            var members = new List<MemberDeclarationSyntax>();
            for (int i = 0; i < node.Members.Count; i++)
            {
                if (node.Members[i] is GlobalStatementSyntax gs && MakeHook(gs.Statement) is { } hook)
                    members.Add(GlobalStatement(hook));
                members.Add(visited.Members[i]);
            }
            return visited.WithMembers(List(members));
        }

        private SyntaxList<StatementSyntax> Interleave(SyntaxList<StatementSyntax> original, SyntaxList<StatementSyntax> visited)
        {
            var list = new List<StatementSyntax>(original.Count * 2);
            for (int i = 0; i < original.Count; i++)
            {
                if (MakeHook(original[i]) is { } hook) list.Add(hook);
                list.Add(visited[i]);
            }
            return List(list);
        }

        // ── 內嵌陳述式（if/while/for… 後面沒有大括號的情況）：包成區塊 ──

        private StatementSyntax Embed(StatementSyntax original, StatementSyntax visited)
        {
            if (original is BlockSyntax) return visited;
            var hook = MakeHook(original);
            return hook is null ? visited : Block(hook, visited);
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var v = (IfStatementSyntax)base.VisitIfStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitElseClause(ElseClauseSyntax node)
        {
            var v = (ElseClauseSyntax)base.VisitElseClause(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var v = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var v = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var v = (ForStatementSyntax)base.VisitForStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var v = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var v = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
        {
            var v = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
        {
            var v = (LockStatementSyntax)base.VisitLockStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
        {
            var v = (FixedStatementSyntax)base.VisitFixedStatement(node)!;
            return v.WithStatement(Embed(node.Statement, v.Statement));
        }

        // ── 運算式主體的方法 / 區域函式：轉為區塊主體以便插樁 ──

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var v = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            if (node.ExpressionBody is null) return v;
            var body = ExpressionToBody(node, node.ExpressionBody.Expression, v.ExpressionBody!.Expression);
            return body is null ? v : v.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body);
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var v = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            if (node.ExpressionBody is null) return v;
            var body = ExpressionToBody(node, node.ExpressionBody.Expression, v.ExpressionBody!.Expression);
            return body is null ? v : v.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body);
        }

        private BlockSyntax? ExpressionToBody(SyntaxNode function, ExpressionSyntax original, ExpressionSyntax visited)
        {
            if (original is RefExpressionSyntax) return null;
            if (_model.GetDeclaredSymbol(function) is not IMethodSymbol m) return null;

            StatementSyntax stmt;
            if (visited is ThrowExpressionSyntax te)
                stmt = ThrowStatement(te.Expression);
            else if (m.ReturnsVoid || (m.IsAsync && m.ReturnType is INamedTypeSymbol { IsGenericType: false }))
                stmt = ExpressionStatement(visited);
            else
                stmt = ReturnStatement(visited);

            var stmts = new List<StatementSyntax>();
            if (MakeHook(original) is { } hook) stmts.Add(hook);
            stmts.Add(stmt);
            var block = Block(stmts);
            return WrapFrame(function, block, FunctionName(function));
        }

        // ── 呼叫堆疊框架 ──

        private static bool IsFunctionBody(BlockSyntax node) => node.Parent is
            BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or
            AnonymousFunctionExpressionSyntax;

        private BlockSyntax WrapFrame(BlockSyntax originalBody, BlockSyntax visited)
        {
            if (ContainsYield(originalBody)) return visited; // 迭代器不追蹤框架
            return WrapFrame(originalBody.Parent!, visited, FunctionName(originalBody.Parent!));
        }

        private static BlockSyntax WrapFrame(SyntaxNode function, BlockSyntax body, string name)
        {
            var enter = ParseStatement($"{Rt}.Enter({Literal(name)});");
            var exit = ParseStatement($"{Rt}.Exit();");
            return Block(enter, TryStatement(body, default, FinallyClause(Block(exit))));
        }

        private static bool ContainsYield(SyntaxNode body) =>
            body.DescendantNodes(n => n == body || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .OfType<YieldStatementSyntax>().Any();

        private static string FunctionName(SyntaxNode fn)
        {
            static string Zh(string s) => KeywordDictionary.CSharpToChinese.TryGetValue(s, out var z) ? z : s;
            string TypeName() => fn.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText is { } t ? Zh(t) : "";

            return fn switch
            {
                MethodDeclarationSyntax m => $"{TypeName()}的{Zh(m.Identifier.ValueText)}",
                ConstructorDeclarationSyntax => $"{TypeName()}（建構函式）",
                DestructorDeclarationSyntax => $"{TypeName()}（解構函式）",
                OperatorDeclarationSyntax o => $"{TypeName()}的運算子 {o.OperatorToken.Text}",
                ConversionOperatorDeclarationSyntax => $"{TypeName()}（轉換運算子）",
                AccessorDeclarationSyntax a => $"{TypeName()}的{Zh(AccessorOwner(a))}（{Zh(a.Keyword.ValueText)}）",
                LocalFunctionStatementSyntax l => $"{Zh(l.Identifier.ValueText)}（區域函式）",
                AnonymousFunctionExpressionSyntax => "（匿名函式）",
                _ => "?",
            };
        }

        private static string AccessorOwner(AccessorDeclarationSyntax a) => a.Parent?.Parent switch
        {
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            EventDeclarationSyntax e => e.Identifier.ValueText,
            IndexerDeclarationSyntax => "索引子",
            _ => "?",
        };

        private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

        // ── 掛鉤：行號 + 此時可讀取的區域變數 / 參數 ──

        private StatementSyntax? MakeHook(StatementSyntax stmt)
        {
            if (stmt is LocalFunctionStatementSyntax or BlockSyntax or EmptyStatementSyntax) return null;
            return MakeHook((SyntaxNode)stmt);
        }

        private StatementSyntax MakeHook(SyntaxNode node)
        {
            int line = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
            var vars = CollectVariables(node);

            string code;
            if (vars.Count == 0)
            {
                code = $"{Rt}.H({line}, null, null);";
            }
            else
            {
                var names = string.Join(", ", vars.Select(v => Literal(v.display)));
                var values = string.Join(", ", vars.Select(v => v.expr));
                code = $"{Rt}.H({line}, new string[] {{ {names} }}, new object[] {{ {values} }});";
            }
            return ParseStatement(code).WithAdditionalAnnotations(HookAnnotation)
                .WithTrailingTrivia(ElasticCarriageReturnLineFeed);
        }

        private List<(string display, string expr)> CollectVariables(SyntaxNode node)
        {
            var result = new List<(string, string)>();
            int pos = node.SpanStart;
            var enclosing = _model.GetEnclosingSymbol(pos);

            DataFlowAnalysis? flow = null;
            try
            {
                flow = node is ExpressionSyntax e ? _model.AnalyzeDataFlow(e) : _model.AnalyzeDataFlow((StatementSyntax)node);
                if (flow is { Succeeded: false }) flow = null;
            }
            catch
            {
                flow = null;
            }
            var assigned = flow?.DefinitelyAssignedOnEntry ?? System.Collections.Immutable.ImmutableArray<ISymbol>.Empty;

            bool inStaticLambda = enclosing is IMethodSymbol { IsStatic: true, MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction };
            bool enclosingIsNested = enclosing is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction };

            var seen = new HashSet<string>();
            foreach (var sym in _model.LookupSymbols(pos))
            {
                ITypeSymbol? type;
                bool ok;
                bool outer = !SymbolEqualityComparer.Default.Equals(sym.ContainingSymbol, enclosing);

                switch (sym)
                {
                    case ILocalSymbol l:
                        type = l.Type;
                        ok = assigned.Contains(l, SymbolEqualityComparer.Default)
                             && !(outer && enclosingIsNested && (l.IsRef || inStaticLambda));
                        break;
                    case IParameterSymbol p:
                        type = p.Type;
                        ok = (p.RefKind != RefKind.Out || assigned.Contains(p, SymbolEqualityComparer.Default))
                             && !(outer && enclosingIsNested && (p.RefKind != RefKind.None || inStaticLambda));
                        break;
                    default:
                        continue;
                }

                if (!ok || type is null || type.IsRefLikeType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
                    continue;
                if (!seen.Add(sym.Name)) continue;

                string display = KeywordDictionary.CSharpToChinese.TryGetValue(sym.Name, out var zh) ? zh : sym.Name;
                result.Add((display, "@" + sym.Name));
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));

            if (CanUseThis(node, enclosing))
                result.Insert(0, ("這個", "this"));
            return result;
        }

        private static bool CanUseThis(SyntaxNode node, ISymbol? enclosing)
        {
            if (node.Ancestors().Any(a => a is EqualsValueClauseSyntax or ConstructorInitializerSyntax or AttributeSyntax))
                return false;
            if (node.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any(f => f.Modifiers.Any(SyntaxKind.StaticKeyword)))
                return false;
            if (node.Ancestors().OfType<LocalFunctionStatementSyntax>().Any(f => f.Modifiers.Any(SyntaxKind.StaticKeyword)))
                return false;

            var s = enclosing;
            bool nested = false;
            while (s is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            {
                nested = true;
                s = s.ContainingSymbol;
            }
            if (s is not IMethodSymbol m || m.IsStatic) return false;
            if (m.ContainingType is null) return false;
            if (m.ContainingType.TypeKind == TypeKind.Struct && nested) return false;
            return m.ContainingType.TypeKind is TypeKind.Class or TypeKind.Struct;
        }
    }
}
