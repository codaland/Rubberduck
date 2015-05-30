﻿using Antlr4.Runtime.Misc;
using Microsoft.Vbe.Interop;
using Rubberduck.Parsing;
using Rubberduck.Parsing.Grammar;
using Rubberduck.Parsing.Symbols;
using Rubberduck.UI;
using Rubberduck.UI.Refactorings.ReorderParameters;
using Rubberduck.VBA;
using Rubberduck.VBEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Rubberduck.Refactoring.ReorderParametersRefactoring
{
    public class ReorderParametersRefactoring : IRefactoring
    {
        private readonly VBProjectParseResult _parseResult;
        private readonly Declarations _declarations;
        private List<Parameter> Parameters { get; set; }
        public Declaration TargetDeclaration { get; set; }

        public ReorderParametersRefactoring(VBProjectParseResult parseResult, Declaration target, List<Parameter> parameters)
        {
            _parseResult = parseResult;
            _declarations = parseResult.Declarations;

            if (!ValidDeclarationTypes.Contains(target.DeclarationType))
            {
                throw new ArgumentException("Invalid declaration type.");
            }

            TargetDeclaration = target;
            Parameters = parameters;
        }

        public void Refactor()
        {
            TargetDeclaration = PromptIfTargetImplementsInterface();

            if (TargetDeclaration == null || !Parameters.Where((param, index) => param.Index != index).Any() || !IsValidParamOrder())
            {
                return;
            }

            AdjustReferences(TargetDeclaration.References);
            AdjustSignatures();
        }

        private bool IsValidParamOrder()
        {
            var indexOfFirstOptionalParam = Parameters.FindIndex(param => param.IsOptional);
            if (indexOfFirstOptionalParam >= 0)
            {
                for (var index = indexOfFirstOptionalParam + 1; index < Parameters.Count; index++)
                {
                    if (!Parameters.ElementAt(index).IsOptional)
                    {
                        MessageBox.Show(RubberduckUI.ReorderPresenter_OptionalParametersMustBeLastError, RubberduckUI.ReorderParamsDialog_TitleText, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return false;
                    }
                }
            }

            var indexOfParamArray = Parameters.FindIndex(param => param.IsParamArray);
            if (indexOfParamArray >= 0)
            {
                if (indexOfParamArray != Parameters.Count - 1)
                {
                    MessageBox.Show(RubberduckUI.ReorderPresenter_ParamArrayError, RubberduckUI.ReorderParamsDialog_TitleText, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
            return true;
        }

        private void AdjustReferences(IEnumerable<IdentifierReference> references)
        {
            foreach (var reference in references.Where(item => item.Context != TargetDeclaration.Context))
            {
                var proc = (dynamic)reference.Context.Parent;
                var module = reference.QualifiedModuleName.Component.CodeModule;

                // This is to prevent throws when this statement fails:
                // (VBAParser.ArgsCallContext)proc.argsCall();
                try
                {
                    var check = (VBAParser.ArgsCallContext)proc.argsCall();
                }
                catch
                {
                    continue;
                }

                var paramList = (VBAParser.ArgsCallContext)proc.argsCall();

                if (paramList == null)
                {
                    continue;
                }

                RewriteCall(paramList, module);
            }
        }

        private void RewriteCall(VBAParser.ArgsCallContext paramList, CodeModule module)
        {
            var paramNames = paramList.argCall().Select(arg => arg.GetText()).ToList();

            var lineCount = paramList.Stop.Line - paramList.Start.Line + 1; // adjust for total line count

            var parameterIndex = 0;
            for (var line = paramList.Start.Line; line < paramList.Start.Line + lineCount; line++)
            {
                var newContent = module.Lines[line, 1].Replace(" , ", "");

                var currentStringIndex = 0;

                for (var i = 0; i < paramNames.Count && parameterIndex < Parameters.Count; i++)
                {
                    var parameterStringIndex = newContent.IndexOf(paramNames.ElementAt(i), currentStringIndex);

                    if (parameterStringIndex > -1)
                    {
                        if (Parameters.ElementAt(parameterIndex).Index >= paramNames.Count)
                        {
                            newContent = newContent.Insert(parameterStringIndex, " , ");
                            i--;
                            parameterIndex++;
                            continue;
                        }

                        var oldParameterString = paramNames.ElementAt(i);
                        var newParameterString = paramNames.ElementAt(Parameters.ElementAt(parameterIndex).Index);
                        var beginningSub = newContent.Substring(0, parameterStringIndex);
                        var replaceSub = newContent.Substring(parameterStringIndex).Replace(oldParameterString, newParameterString);

                        newContent = beginningSub + replaceSub;

                        parameterIndex++;
                        currentStringIndex = beginningSub.Length + newParameterString.Length;
                    }
                }

                module.ReplaceLine(line, newContent);
            }
        }

        private void AdjustSignatures()
        {
            var proc = (dynamic)TargetDeclaration.Context;
            var paramList = (VBAParser.ArgListContext)proc.argList();
            var module = TargetDeclaration.QualifiedName.QualifiedModuleName.Component.CodeModule;

            // if we are reordering a property getter, check if we need to reorder a letter/setter too
            if (TargetDeclaration.DeclarationType == DeclarationType.PropertyGet)
            {
                var setter = _declarations.Items.FirstOrDefault(item => item.ParentScope == TargetDeclaration.ParentScope &&
                                              item.IdentifierName == TargetDeclaration.IdentifierName &&
                                              item.DeclarationType == DeclarationType.PropertySet);

                if (setter != null)
                {
                    AdjustSignatures(setter);
                }

                var letter = _declarations.Items.FirstOrDefault(item => item.ParentScope == TargetDeclaration.ParentScope &&
                              item.IdentifierName == TargetDeclaration.IdentifierName &&
                              item.DeclarationType == DeclarationType.PropertyLet);

                if (letter != null)
                {
                    AdjustSignatures(letter);
                }
            }

            RewriteSignature(TargetDeclaration, paramList, module);

            foreach (var withEvents in _declarations.Items.Where(item => item.IsWithEvents && item.AsTypeName == TargetDeclaration.ComponentName))
            {
                foreach (var reference in _declarations.FindEventProcedures(withEvents))
                {
                    AdjustReferences(reference.References);
                    AdjustSignatures(reference);
                }
            }

            var interfaceImplementations = _declarations.FindInterfaceImplementationMembers()
                                                        .Where(item => item.Project.Equals(TargetDeclaration.Project) &&
                                                               item.IdentifierName == TargetDeclaration.ComponentName + "_" + TargetDeclaration.IdentifierName);
            foreach (var interfaceImplentation in interfaceImplementations)
            {
                AdjustReferences(interfaceImplentation.References);
                AdjustSignatures(interfaceImplentation);
            }
        }

        private void AdjustSignatures(Declaration declaration)
        {
            var proc = (dynamic)declaration.Context.Parent;
            var module = declaration.QualifiedName.QualifiedModuleName.Component.CodeModule;
            VBAParser.ArgListContext paramList;

            if (declaration.DeclarationType == DeclarationType.PropertySet || declaration.DeclarationType == DeclarationType.PropertyLet)
            {
                paramList = (VBAParser.ArgListContext)proc.children[0].argList();
            }
            else
            {
                paramList = (VBAParser.ArgListContext)proc.subStmt().argList();
            }

            RewriteSignature(declaration, paramList, module);
        }

        private void RewriteSignature(Declaration target, VBAParser.ArgListContext paramList, CodeModule module)
        {
            var argList = paramList.arg();

            var newContent = GetOldSignature(target);
            var lineNum = paramList.GetSelection().LineCount;

            var parameterIndex = 0;

            var currentStringIndex = 0;

            for (var i = parameterIndex; i < Parameters.Count; i++)
            {
                var oldParam = argList.ElementAt(parameterIndex).GetText();
                var newParam = argList.ElementAt(Parameters.ElementAt(parameterIndex).Index).GetText();
                var parameterStringIndex = newContent.IndexOf(oldParam, currentStringIndex);

                if (parameterStringIndex > -1)
                {
                    var beginningSub = newContent.Substring(0, parameterStringIndex);
                    var replaceSub = newContent.Substring(parameterStringIndex).Replace(oldParam, newParam);

                    newContent = beginningSub + replaceSub;

                    parameterIndex++;
                    currentStringIndex = beginningSub.Length + newParam.Length;
                }
            }

            module.ReplaceLine(paramList.Start.Line, newContent);
            module.DeleteLines(paramList.Start.Line + 1, lineNum - 1);
        }

        private string GetOldSignature(Declaration target)
        {
            var targetModule = _parseResult.ComponentParseResults.SingleOrDefault(m => m.QualifiedName == target.QualifiedName.QualifiedModuleName);
            if (targetModule == null)
            {
                return null;
            }

            var rewriter = targetModule.GetRewriter();

            var context = target.Context;
            var firstTokenIndex = context.Start.TokenIndex;
            var lastTokenIndex = -1; // will blow up if this code runs for any context other than below

            var subStmtContext = context as VBAParser.SubStmtContext;
            if (subStmtContext != null)
            {
                lastTokenIndex = subStmtContext.argList().RPAREN().Symbol.TokenIndex;
            }

            var functionStmtContext = context as VBAParser.FunctionStmtContext;
            if (functionStmtContext != null)
            {
                lastTokenIndex = functionStmtContext.asTypeClause() != null
                    ? functionStmtContext.asTypeClause().Stop.TokenIndex
                    : functionStmtContext.argList().RPAREN().Symbol.TokenIndex;
            }

            var propertyGetStmtContext = context as VBAParser.PropertyGetStmtContext;
            if (propertyGetStmtContext != null)
            {
                lastTokenIndex = propertyGetStmtContext.asTypeClause() != null
                    ? propertyGetStmtContext.asTypeClause().Stop.TokenIndex
                    : propertyGetStmtContext.argList().RPAREN().Symbol.TokenIndex;
            }

            var propertyLetStmtContext = context as VBAParser.PropertyLetStmtContext;
            if (propertyLetStmtContext != null)
            {
                lastTokenIndex = propertyLetStmtContext.argList().RPAREN().Symbol.TokenIndex;
            }

            var propertySetStmtContext = context as VBAParser.PropertySetStmtContext;
            if (propertySetStmtContext != null)
            {
                lastTokenIndex = propertySetStmtContext.argList().RPAREN().Symbol.TokenIndex;
            }

            var declareStmtContext = context as VBAParser.DeclareStmtContext;
            if (declareStmtContext != null)
            {
                lastTokenIndex = declareStmtContext.STRINGLITERAL().Last().Symbol.TokenIndex;
                if (declareStmtContext.argList() != null)
                {
                    lastTokenIndex = declareStmtContext.argList().RPAREN().Symbol.TokenIndex;
                }
                if (declareStmtContext.asTypeClause() != null)
                {
                    lastTokenIndex = declareStmtContext.asTypeClause().Stop.TokenIndex;
                }
            }

            var eventStmtContext = context as VBAParser.EventStmtContext;
            if (eventStmtContext != null)
            {
                lastTokenIndex = eventStmtContext.argList().RPAREN().Symbol.TokenIndex;
            }

            return rewriter.GetText(new Interval(firstTokenIndex, lastTokenIndex));
        }

        public Declaration AcquireTarget(QualifiedSelection selection)
        {
            return TargetDeclaration;
        }

        private Declaration PromptIfTargetImplementsInterface()
        {
            var declaration = TargetDeclaration;
            var interfaceImplementation = _declarations.FindInterfaceImplementationMembers().SingleOrDefault(m => m.Equals(declaration));
            if (declaration == null || interfaceImplementation == null)
            {
                return declaration;
            }

            var interfaceMember = _declarations.FindInterfaceMember(interfaceImplementation);
            var message = string.Format(RubberduckUI.ReorderPresenter_TargetIsInterfaceMemberImplementation, declaration.IdentifierName, interfaceMember.ComponentName, interfaceMember.IdentifierName);

            var confirm = MessageBox.Show(message, RubberduckUI.ReorderParamsDialog_TitleText, MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation);
            if (confirm == DialogResult.No)
            {
                return null;
            }

            return interfaceMember;
        }

        public IEnumerable<Parameter> MethodParameters()
        {
            var procedure = (dynamic)TargetDeclaration.Context;
            var argList = (VBAParser.ArgListContext)procedure.argList();
            var args = argList.arg();

            var index = 0;
            return args.Select(arg => new Parameter(arg.GetText().RemoveExtraSpaces(), index++));
        }

        private static readonly DeclarationType[] ValidDeclarationTypes =
        {
            DeclarationType.Event,
            DeclarationType.Function,
            DeclarationType.Procedure,
            DeclarationType.PropertyGet,
            DeclarationType.PropertyLet,
            DeclarationType.PropertySet
        };
    }
}
