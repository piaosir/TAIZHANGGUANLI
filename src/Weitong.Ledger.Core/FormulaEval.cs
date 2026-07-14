using System.Globalization;

namespace Weitong.Ledger.Core;

/// <summary>
/// 单元格「简单公式」求值器：仅支持 = + - * / ( ) 与小数（含负号、括号）。
/// 不是电子表格引擎——不支持单元格引用(A1)、函数，只做一次性算术：录入 =100*12+50 → 得 1250。
/// 纯逻辑、无 UI 依赖，可独立单测。
/// </summary>
public static class FormulaEval
{
    /// <summary>是否以 '='（或全角 '＝'）开头，视为公式。</summary>
    public static bool IsFormula(string? input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        var s = input.TrimStart();
        return s.Length > 0 && (s[0] == '=' || s[0] == '＝');
    }

    /// <summary>
    /// 求值。<paramref name="input"/> 可带或不带前导 '='。成功返回 true 并给出结果；
    /// 语法错误、除零、溢出一律返回 false（调用方据此保留原值/标红）。
    /// </summary>
    public static bool TryEvaluate(string? input, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = Normalize(input.Trim());
        if (s.Length > 0 && (s[0] == '=' || s[0] == '＝')) s = s[1..];
        s = s.Trim();
        if (s.Length == 0) return false;

        try
        {
            var p = new Parser(s);
            var v = p.ParseExpression();
            if (!p.AtEnd) return false;          // 尾部残留 → 语法错误
            result = v;
            return true;
        }
        catch
        {
            return false;                         // 除零/溢出/非法字符
        }
    }

    /// <summary>全角符号归一、去掉千分位与货币符，便于「=1,200＋¥300」这类粘贴内容求值。</summary>
    private static string Normalize(string s)
    {
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (var ch in s)
        {
            char c = ch switch
            {
                '（' => '(', '）' => ')',
                '＋' => '+', '－' => '-', 'ー' => '-', '−' => '-',
                '×' => '*', '＊' => '*', '÷' => '/', '／' => '/',
                '．' => '.',
                _ => ch,
            };
            if (c is ',' or '，' or '¥' or '￥') continue;  // 丢弃千分位与货币符（空白保留，作 token 分隔）
            buf[n++] = c;
        }
        return new string(buf[..n]);
    }

    /// <summary>递归下降：expr = term (('+'|'-') term)*；term = factor (('*'|'/') factor)*；
    /// factor = ('+'|'-') factor | number | '(' expr ')'。</summary>
    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _s;
        private int _i;
        public Parser(string s) { _s = s; _i = 0; }
        public bool AtEnd { get { SkipWs(); return _i >= _s.Length; } }
        private char Cur => _s[_i];
        private void SkipWs() { while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++; }

        public decimal ParseExpression()
        {
            var v = ParseTerm();
            while (!AtEnd && (Cur == '+' || Cur == '-'))
            {
                char op = Cur; _i++;
                var r = ParseTerm();
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        private decimal ParseTerm()
        {
            var v = ParseFactor();
            while (!AtEnd && (Cur == '*' || Cur == '/'))
            {
                char op = Cur; _i++;
                var r = ParseFactor();
                v = op == '*' ? v * r : v / r;     // 除零 → DivideByZeroException → TryEvaluate 返回 false
            }
            return v;
        }

        private decimal ParseFactor()
        {
            if (AtEnd) throw new FormatException("空表达式");
            if (Cur == '+') { _i++; return ParseFactor(); }
            if (Cur == '-') { _i++; return -ParseFactor(); }
            if (Cur == '(')
            {
                _i++;
                var v = ParseExpression();
                if (AtEnd || Cur != ')') throw new FormatException("括号不匹配");
                _i++;
                return v;
            }
            return ParseNumber();
        }

        private decimal ParseNumber()
        {
            SkipWs();
            int start = _i;
            bool dot = false;
            // 注意：这里不能用会跳空白的 AtEnd，否则「1 2」会被连成「12」
            while (_i < _s.Length && (char.IsAsciiDigit(_s[_i]) || _s[_i] == '.'))
            {
                if (_s[_i] == '.') { if (dot) throw new FormatException("多个小数点"); dot = true; }
                _i++;
            }
            if (_i == start) throw new FormatException("缺少数字");
            var slice = _s[start.._i];
            return decimal.Parse(slice, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }
    }
}
