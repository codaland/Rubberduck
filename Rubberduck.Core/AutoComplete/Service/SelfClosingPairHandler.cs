﻿using System.Collections.Generic;
using System.Linq;
using Rubberduck.Settings;
using Rubberduck.VBEditor;
using Rubberduck.VBEditor.Events;
using Rubberduck.VBEditor.SourceCodeHandling;

namespace Rubberduck.AutoComplete.Service
{
    public class SelfClosingPairHandler : AutoCompleteHandlerBase
    {
        private readonly IDictionary<char, SelfClosingPair> _selfClosingPairs;
        private readonly SelfClosingPairCompletionService _scpService;

        public SelfClosingPairHandler(ICodePaneHandler pane, SelfClosingPairCompletionService scpService)
            : base(pane)
        {
            var pairs = new[]
            {
                new SelfClosingPair('(', ')'),
                new SelfClosingPair('"', '"'),
                new SelfClosingPair('[', ']'),
                new SelfClosingPair('{', '}'),
            };
            _selfClosingPairs = pairs
                .Select(p => new {Key = p.OpeningChar, Pair = p})
                .Union(pairs.Where(p => !p.IsSymetric).Select(p => new {Key = p.ClosingChar, Pair = p}))
                .ToDictionary(p => p.Key, p => p.Pair);

            _scpService = scpService;
        }

        public override bool Handle(AutoCompleteEventArgs e, AutoCompleteSettings settings, out CodeString result)
        {
            result = null;
            if (!_selfClosingPairs.TryGetValue(e.Character, out var pair) && e.Character != '\b')
            {
                return false;
            }

            var original = CodePaneHandler.GetCurrentLogicalLine(e.Module);
            if (!HandleInternal(e, original, pair, out result))
            {
                return false;
            }

            var snippetPosition = new Selection(result.SnippetPosition.StartLine, 1, result.SnippetPosition.EndLine, 1);
            result = new CodeString(result.Code, result.CaretPosition, snippetPosition);

            e.Handled = true;
            return true;
        }

        private bool HandleInternal(AutoCompleteEventArgs e, CodeString original, SelfClosingPair pair, out CodeString result)
        {
            var isPresent = original.CaretLine.EndsWith($"{pair.OpeningChar}{pair.ClosingChar}");

            if (!_scpService.Execute(pair, original, e.Character, out result))
            {
                return false;
            }

            var prettified = CodePaneHandler.Prettify(e.Module, original);
            if (!isPresent && original.CaretLine.Length + 2 == prettified.CaretLine.Length &&
                prettified.CaretLine.EndsWith($"{pair.OpeningChar}{pair.ClosingChar}"))
            {
                // prettifier just added the pair for us; likely a Sub or Function statement.
                prettified = original; // pretend this didn't happen. note: probably breaks if original has extra whitespace.
            }

            if (!_scpService.Execute(pair, prettified, e.Character, out result))
            {
                return false;
            }

            result = CodePaneHandler.Prettify(e.Module, result);

            var currentLine = result.Lines[result.CaretPosition.StartLine];
            if (!string.IsNullOrWhiteSpace(currentLine) &&
                currentLine.EndsWith(" ") &&
                result.CaretPosition.StartColumn == currentLine.Length)
            {
                result = result.ReplaceLine(result.CaretPosition.StartLine, currentLine.TrimEnd());
            }

            if (pair.OpeningChar == '(' && 
                e.Character == pair.OpeningChar &&
                !result.CaretLine.EndsWith($"{pair.OpeningChar}{pair.ClosingChar}"))
            {
                // VBE eats it. just bail out.
                result = null;
                return false;
            }

            return true;
        }
    }
}