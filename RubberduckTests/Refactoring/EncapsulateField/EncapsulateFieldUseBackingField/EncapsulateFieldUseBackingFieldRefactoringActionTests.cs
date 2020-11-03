﻿using NUnit.Framework;
using Rubberduck.Common;
using Rubberduck.Parsing.Rewriter;
using Rubberduck.Parsing.Symbols;
using Rubberduck.Parsing.VBA;
using Rubberduck.Refactorings;
using Rubberduck.Refactorings.EncapsulateField;
using Rubberduck.Refactorings.EncapsulateFieldUseBackingField;
using System.Collections.Generic;
using System.Linq;

namespace RubberduckTests.Refactoring.EncapsulateField.EncapsulateFieldUseBackingField
{
    [TestFixture]
    public class EncapsulateFieldUseBackingFieldRefactoringActionTests : RefactoringActionTestBase<EncapsulateFieldUseBackingFieldModel>
    {
        private EncapsulateFieldTestSupport Support { get; } = new EncapsulateFieldTestSupport();

        [SetUp]
        public void ExecutesBeforeAllTests()
        {
            Support.ResetResolver();
        }

        [TestCase(false, "Name")]
        [TestCase(true, "Name")]
        [TestCase(false, null)]
        [TestCase(true, null)]
        [Category("Refactorings")]
        [Category("Encapsulate Field")]
        [Category(nameof(EncapsulateFieldUseBackingFieldRefactoringAction))]
        public void EncapsulatePublicField(bool isReadOnly, string propertyIdentifier)
        {
            var target = "fizz";
            var inputCode = $"Public {target} As Integer";

            EncapsulateFieldUseBackingFieldModel modelBuilder(RubberduckParserState state)
            {
                var modelFactory = Support.Resolve<IEncapsulateFieldUseBackingFieldModelFactory>(state);

                var field = state.DeclarationFinder.MatchName(target).Single();
                var fieldModel = new FieldEncapsulationModel(field as VariableDeclaration, isReadOnly, propertyIdentifier);
                return modelFactory.Create( new List<FieldEncapsulationModel>() { fieldModel });
            }

            var refactoredCode = RefactoredCode(inputCode, modelBuilder);

            var resultPropertyIdentifier = propertyIdentifier ?? target.CapitalizeFirstLetter();

            var backingField = propertyIdentifier != null
                ? target
                : $"{target}1";

            StringAssert.Contains($"Public Property Get {resultPropertyIdentifier}()", refactoredCode);
            StringAssert.Contains($"{resultPropertyIdentifier} = {backingField}", refactoredCode);

            if (isReadOnly)
            {
                StringAssert.DoesNotContain($"Public Property Let {resultPropertyIdentifier}(", refactoredCode);
                StringAssert.DoesNotContain($"{backingField} = ", refactoredCode);
            }
            else
            {
                StringAssert.Contains($"Public Property Let {resultPropertyIdentifier}(", refactoredCode);
                StringAssert.Contains($"{backingField} = ", refactoredCode);
            }
        }

        [Test]
        [Category("Refactorings")]
        [Category("Encapsulate Field")]
        [Category(nameof(EncapsulateFieldUseBackingFieldRefactoringAction))]
        public void EmptyTargetSet()
        {
            var target = "fizz";
            var inputCode = $"Public {target} As Integer";

            EncapsulateFieldUseBackingFieldModel modelBuilder(RubberduckParserState state)
            {
                var modelFactory = Support.Resolve<IEncapsulateFieldUseBackingFieldModelFactory>(state);
                return modelFactory.Create(Enumerable.Empty<FieldEncapsulationModel>());
            }

            var refactoredCode = RefactoredCode(inputCode, modelBuilder);
            Assert.AreEqual(refactoredCode, inputCode);
        }

        protected override IRefactoringAction<EncapsulateFieldUseBackingFieldModel> TestBaseRefactoring(RubberduckParserState state, IRewritingManager rewritingManager)
        {
            return Support.Resolve<EncapsulateFieldUseBackingFieldRefactoringAction>(state, rewritingManager);
        }
    }
}
