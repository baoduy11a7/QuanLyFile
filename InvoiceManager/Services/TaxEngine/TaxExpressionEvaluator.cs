using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace InvoiceManager.Services.TaxEngine
{
    public interface ITaxExpressionEvaluator
    {
        decimal Evaluate(string expression, IReadOnlyDictionary<string, decimal> context);
        void DetectCyclicDependencies(IReadOnlyDictionary<string, string> fieldFormulas);
        Dictionary<string, decimal> EvaluateAllFields(
            IEnumerable<TaxFormField> fields,
            Dictionary<string, decimal> initialValues);
    }

    /// <summary>
    /// Engine tính toán biểu thức an toàn 100% trên kiểu Decimal
    /// Không sử dụng eval hay Roslyn Compiler để đảm bảo an toàn tuyệt đối, tránh code injection
    /// </summary>
    public class TaxExpressionEvaluator : ITaxExpressionEvaluator
    {
        private static readonly Regex FieldRegex = new(@"\[[a-zA-Z0-9_]+\]", RegexOptions.Compiled);

        /// <summary>
        /// Phát hiện vòng phụ thuộc tuần hoàn (Cyclic Dependency) giữa các chỉ tiêu
        /// </summary>
        public void DetectCyclicDependencies(IReadOnlyDictionary<string, string> fieldFormulas)
        {
            var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in fieldFormulas)
            {
                var target = NormalizeKey(kvp.Key);
                graph[target] = new List<string>();

                if (string.IsNullOrWhiteSpace(kvp.Value)) continue;

                var matches = FieldRegex.Matches(kvp.Value);
                foreach (Match m in matches)
                {
                    var dep = NormalizeKey(m.Value);
                    if (!graph[target].Contains(dep))
                    {
                        graph[target].Add(dep);
                    }
                }
            }

            var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0: unvisited, 1: visiting, 2: visited

            foreach (var node in graph.Keys)
            {
                if (!visited.ContainsKey(node) || visited[node] == 0)
                {
                    if (HasCycleDfs(node, graph, visited, out var cyclePath))
                    {
                        throw new InvalidOperationException($"Phát hiện phụ thuộc vòng giữa các chỉ tiêu: {string.Join(" -> ", cyclePath)}");
                    }
                }
            }
        }

        private static bool HasCycleDfs(string current, Dictionary<string, List<string>> graph, Dictionary<string, int> visited, out List<string> cyclePath)
        {
            visited[current] = 1; // Đang duyệt (In stack)

            if (graph.TryGetValue(current, out var neighbors))
            {
                foreach (var next in neighbors)
                {
                    if (visited.TryGetValue(next, out var st) && st == 1)
                    {
                        cyclePath = new List<string> { current, next };
                        return true;
                    }

                    if (!visited.ContainsKey(next) || visited[next] == 0)
                    {
                        if (HasCycleDfs(next, graph, visited, out cyclePath))
                        {
                            cyclePath.Insert(0, current);
                            return true;
                        }
                    }
                }
            }

            visited[current] = 2; // Đã duyệt xong
            cyclePath = new List<string>();
            return false;
        }

        /// <summary>
        /// Tính toán toàn bộ các chỉ tiêu theo thứ tự phụ thuộc (Topological order)
        /// </summary>
        public Dictionary<string, decimal> EvaluateAllFields(
            IEnumerable<TaxFormField> fields,
            Dictionary<string, decimal> initialValues)
        {
            var results = new Dictionary<string, decimal>(initialValues, StringComparer.OrdinalIgnoreCase);
            var formulaMap = fields
                .Where(f => !string.IsNullOrWhiteSpace(f.ExpressionFormula))
                .ToDictionary(f => f.Code, f => f.ExpressionFormula, StringComparer.OrdinalIgnoreCase);

            // Kiểm tra cyclic dependency trước
            DetectCyclicDependencies(formulaMap);

            // Sắp xếp thứ tự tính toán theo topological sort
            var executionOrder = BuildTopologicalOrder(formulaMap);

            foreach (var fieldCode in executionOrder)
            {
                if (formulaMap.TryGetValue(fieldCode, out var formula))
                {
                    decimal val = Evaluate(formula, results);
                    results[fieldCode] = Math.Round(val, 0, MidpointRounding.AwayFromZero);
                }
            }

            return results;
        }

        private static List<string> BuildTopologicalOrder(Dictionary<string, string> formulaMap)
        {
            var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var k in formulaMap.Keys)
            {
                inDegree[k] = 0;
                adj[k] = new List<string>();
            }

            foreach (var kvp in formulaMap)
            {
                var target = kvp.Key;
                var matches = FieldRegex.Matches(kvp.Value);
                foreach (Match m in matches)
                {
                    var dep = m.Value;
                    if (formulaMap.ContainsKey(dep))
                    {
                        if (!adj.ContainsKey(dep)) adj[dep] = new List<string>();
                        adj[dep].Add(target);
                        inDegree[target]++;
                    }
                }
            }

            var queue = new Queue<string>(inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
            var order = new List<string>();

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                order.Add(curr);

                if (adj.TryGetValue(curr, out var list))
                {
                    foreach (var next in list)
                    {
                        inDegree[next]--;
                        if (inDegree[next] == 0)
                        {
                            queue.Enqueue(next);
                        }
                    }
                }
            }

            if (order.Count < formulaMap.Count)
            {
                foreach (var k in formulaMap.Keys)
                {
                    if (!order.Contains(k, StringComparer.OrdinalIgnoreCase))
                    {
                        order.Add(k);
                    }
                }
            }

            return order;
        }

        /// <summary>
        /// Tính giá trị biểu thức dạng toán học an toàn
        /// Hỗ trợ +, -, *, /, MAX(a, b), MIN(a, b), [21], [22]...
        /// </summary>
        public decimal Evaluate(string expression, IReadOnlyDictionary<string, decimal> context)
        {
            if (string.IsNullOrWhiteSpace(expression)) return 0m;

            // 1. Thay thế các biến [Code] bằng giá trị số Decimal
            string resolved = FieldRegex.Replace(expression, m =>
            {
                string key = m.Value;
                if (context.TryGetValue(key, out var val))
                {
                    return val.ToString(CultureInfo.InvariantCulture);
                }
                return "0";
            });

            // 2. Parse biểu thức toán học
            return EvaluateMathExpression(resolved);
        }

        private static decimal EvaluateMathExpression(string expr)
        {
            var tokens = Tokenize(expr);
            var rpn = ConvertToRpn(tokens);
            return EvaluateRpn(rpn);
        }

        private static List<string> Tokenize(string expr)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < expr.Length)
            {
                char c = expr[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || c == '.')
                {
                    int start = i;
                    while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.')) i++;
                    tokens.Add(expr.Substring(start, i - start));
                }
                else if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < expr.Length && char.IsLetterOrDigit(expr[i])) i++;
                    tokens.Add(expr.Substring(start, i - start).ToUpperInvariant());
                }
                else if ("+-*/(),".Contains(c))
                {
                    // Xử lý dấu âm một ngôi (Unary minus)
                    if (c == '-' && (tokens.Count == 0 || tokens.Last() == "(" || tokens.Last() == "," || "+-*/".Contains(tokens.Last())))
                    {
                        tokens.Add("0");
                        tokens.Add("-");
                    }
                    else
                    {
                        tokens.Add(c.ToString());
                    }
                    i++;
                }
                else
                {
                    i++;
                }
            }
            return tokens;
        }

        private static List<string> ConvertToRpn(List<string> tokens)
        {
            var output = new List<string>();
            var opStack = new Stack<string>();

            int GetPrecedence(string op) => op switch
            {
                "+" or "-" => 1,
                "*" or "/" => 2,
                _ => 0
            };

            for (int i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                if (decimal.TryParse(tok, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                {
                    output.Add(tok);
                }
                else if (tok is "MAX" or "MIN" or "ROUND")
                {
                    opStack.Push(tok);
                }
                else if (tok == ",")
                {
                    while (opStack.Count > 0 && opStack.Peek() != "(")
                    {
                        output.Add(opStack.Pop());
                    }
                }
                else if (tok is "+" or "-" or "*" or "/")
                {
                    while (opStack.Count > 0 && GetPrecedence(opStack.Peek()) >= GetPrecedence(tok))
                    {
                        output.Add(opStack.Pop());
                    }
                    opStack.Push(tok);
                }
                else if (tok == "(")
                {
                    opStack.Push(tok);
                }
                else if (tok == ")")
                {
                    while (opStack.Count > 0 && opStack.Peek() != "(")
                    {
                        output.Add(opStack.Pop());
                    }
                    if (opStack.Count > 0 && opStack.Peek() == "(")
                    {
                        opStack.Pop();
                    }
                    if (opStack.Count > 0 && (opStack.Peek() is "MAX" or "MIN" or "ROUND"))
                    {
                        output.Add(opStack.Pop());
                    }
                }
            }

            while (opStack.Count > 0)
            {
                output.Add(opStack.Pop());
            }

            return output;
        }

        private static decimal EvaluateRpn(List<string> rpn)
        {
            var stack = new Stack<decimal>();

            foreach (var tok in rpn)
            {
                if (decimal.TryParse(tok, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                {
                    stack.Push(num);
                }
                else if (tok == "+")
                {
                    decimal b = stack.Count > 0 ? stack.Pop() : 0;
                    decimal a = stack.Count > 0 ? stack.Pop() : 0;
                    stack.Push(a + b);
                }
                else if (tok == "-")
                {
                    decimal b = stack.Count > 0 ? stack.Pop() : 0;
                    decimal a = stack.Count > 0 ? stack.Pop() : 0;
                    stack.Push(a - b);
                }
                else if (tok == "*")
                {
                    decimal b = stack.Count > 0 ? stack.Pop() : 0;
                    decimal a = stack.Count > 0 ? stack.Pop() : 0;
                    stack.Push(a * b);
                }
                else if (tok == "/")
                {
                    decimal b = stack.Count > 0 ? stack.Pop() : 0;
                    decimal a = stack.Count > 0 ? stack.Pop() : 0;
                    stack.Push(b != 0 ? a / b : 0);
                }
                else if (tok == "MAX")
                {
                    decimal b = stack.Count > 0 ? stack.Pop() : 0;
                    decimal a = stack.Count > 0 ? stack.Pop() : 0;
                    stack.Push(Math.Max(a, b));
                }
                else if (tok == "MIN")
                {
                    decimal b = stack.Count > 0 ? stack.Pop() : 0;
                    decimal a = stack.Count > 0 ? stack.Pop() : 0;
                    stack.Push(Math.Min(a, b));
                }
                else if (tok == "ROUND")
                {
                    decimal digits = stack.Count > 0 ? stack.Pop() : 0;
                    decimal a = stack.Count > 0 ? stack.Pop() : 0;
                    stack.Push(Math.Round(a, (int)digits, MidpointRounding.AwayFromZero));
                }
            }

            return stack.Count > 0 ? stack.Pop() : 0m;
        }

        private static string NormalizeKey(string k) => k.Trim().ToUpperInvariant();
    }
}
