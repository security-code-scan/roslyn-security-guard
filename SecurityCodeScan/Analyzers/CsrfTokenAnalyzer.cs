﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SecurityCodeScan.Analyzers.Locale;
using SecurityCodeScan.Analyzers.Utils;
using SecurityCodeScan.Config;

namespace SecurityCodeScan.Analyzers
{
    [SecurityAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    internal class CsrfTokenDiagnosticAnalyzer : SecurityAnalyzer
    {
        public const           string               DiagnosticId = "SCS0016";
        public static readonly DiagnosticDescriptor Rule         = LocaleUtil.GetDescriptor(DiagnosticId);
        public static readonly DiagnosticDescriptor AuditRule    = LocaleUtil.GetDescriptor(DiagnosticId,
                                                                                            titleId: "title_audit",
                                                                                            descriptionId: "description_audit");
        public static readonly DiagnosticDescriptor FromBodyAuditRule = LocaleUtil.GetDescriptor(DiagnosticId,
                                                                                                 titleId: "title_frombody_audit",
                                                                                                 descriptionId: "description_frombody_audit");

        public override         ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

        public override void Initialize(ISecurityAnalysisContext context)
        {
            context.RegisterCompilationStartAction(OnCompilationStartAction);
        }

        private void OnCompilationStartAction(CompilationStartAnalysisContext context, Configuration config)
        {
            var analyzer = new CsrfTokenAnalyzer(config);
            context.RegisterSymbolAction(analyzer.VisitClass, SymbolKind.NamedType);
        }

        private class CsrfTokenAnalyzer
        {
            private readonly Configuration      Configuration;

            public CsrfTokenAnalyzer(Configuration configuration)
            {
                Configuration = configuration;
            }
            
            private static bool HasApplicableAttribute(AttributeData attributeData, Dictionary<string, List<CsrfAttributeCondition>> attributes)
            {
                if (!attributes.Any())
                    return false;

                var name = attributeData.AttributeClass.ToString();

                var args = attributeData.ConstructorArguments;
                var namedArgs = attributeData.NamedArguments;

                if (!attributes.TryGetValue(name, out var conditions))
                    return false;

                foreach (var condition in conditions)
                {
                    var applies =
                        condition.MustMatch.All(
                            c =>
                            {
                                var expectedVal = c.ExpectedValue;
                                var arg = c.ParameterIndexOrPropertyName;
                                TypedConstant? actualVal;
                                if (arg is int argIx)
                                {
                                    // something very weird is happening, freak out
                                    if (argIx >= args.Length)
                                        return false;

                                    actualVal = args[argIx];
                                }
                                else if (arg is string propName)
                                {
                                    actualVal = null;
                                    foreach (var named in namedArgs)
                                    {
                                        if (named.Key.Equals(propName))
                                        {
                                            actualVal = named.Value;
                                            break;
                                        }
                                    }

                                    if (actualVal == null)
                                        return false;
                                }
                                else
                                {
                                    throw new Exception($"Unexpected ParameterIndexOrPropertyName: {arg}");
                                }

                                if (actualVal.Value.IsNull)
                                    return false;

                                return actualVal.Value.Value.Equals(expectedVal);
                            }
                        );

                    if (applies)
                        return true;
                }

                return false;
            }

            private static bool IsAntiForgeryToken(AttributeData attributeData, CsrfNamedGroup group)
            => HasApplicableAttribute(attributeData, group.AntiCsrfAttributes);

            private static bool IsAnonymousAttribute(AttributeData attributeData, CsrfNamedGroup group)
            => HasApplicableAttribute(attributeData, group.AnonymousAttributes);

            private static bool IsHttpMethodAttribute(AttributeData attributeData, CsrfNamedGroup group)
            => HasApplicableAttribute(attributeData, group.HttpMethodAttributes);

            private static bool IsNonActionAttribute(AttributeData attributeData, CsrfNamedGroup group)
            => HasApplicableAttribute(attributeData, group.NonActionAttributes);

            private static bool IsIgnoreAttribute(AttributeData attributeData, CsrfNamedGroup group)
            => HasApplicableAttribute(attributeData, group.IgnoreAttributes);

            private static bool IsActionAttribute(AttributeData attributeData, CsrfNamedGroup group)
            => HasApplicableAttribute(attributeData, group.ActionAttributes);

            private static bool IsDerivedFromController(ITypeSymbol classSymbol, CsrfNamedGroup group)
            {
                if (!group.Controllers.Any())
                    return false;

                foreach(var c in group.Controllers)
                {
                    if (classSymbol.IsDerivedFrom(c))
                        return true;
                }

                return false;
            }

            public void VisitClass(SymbolAnalysisContext ctx)
            {
                var classSymbol = (ITypeSymbol)ctx.Symbol;
                foreach (var group in Configuration.CsrfGoups)
                {
                    VisitClass(ctx, classSymbol, group);
                }
            }

            private void VisitClass(SymbolAnalysisContext ctx, ITypeSymbol classSymbol, CsrfNamedGroup group)
            {
                var isClassControllerDerived = IsDerivedFromController(classSymbol, group);

                // if we're not in a controller, and this group _ONLY_ publishes actions through controllers
                //   quit early
                if (!isClassControllerDerived && !group.ActionAttributes.Any())
                    return;

                bool isClassIgnored = IsClassIgnored(classSymbol, group);

                if (!Configuration.AuditMode && isClassIgnored)
                    return;

                if (IsClassAnonymous(classSymbol, group))
                    return;

                bool classHasAntiForgeryAttribute = classSymbol.HasDerivedClassAttribute(c => IsAntiForgeryToken(c, group));
                
                foreach (var member in classSymbol.GetMembers())
                {
                    if (!(member is IMethodSymbol methodSymbol))
                        continue;

                    var shouldConsiderMethod =
                        isClassControllerDerived || IsMethodAction(methodSymbol, group);
                    
                    if (!shouldConsiderMethod)
                        continue;

                    var isMethodIgnored = false;
                    if (!isClassIgnored)
                        isMethodIgnored = IsMethodIgnored(methodSymbol, group);

                    if (!Configuration.AuditMode && isMethodIgnored)
                        continue;

                    var isArgumentIgnored = false;
                    if (!isClassIgnored && !isMethodIgnored)
                        isArgumentIgnored = IsArgumentIgnored(methodSymbol, classSymbol, group);

                    if (!Configuration.AuditMode && isArgumentIgnored)
                        continue;

                    if (!methodSymbol.HasDerivedMethodAttribute(c => IsHttpMethodAttribute(c, group)))
                        continue;

                    if (methodSymbol.HasDerivedMethodAttribute(c => IsNonActionAttribute(c, group)))
                        continue;

                    if (methodSymbol.HasDerivedMethodAttribute(c => IsAnonymousAttribute(c, group)))
                        continue;

                    if (!classHasAntiForgeryAttribute && !AntiforgeryAttributeExists(methodSymbol, group))
                    {
                        if (isClassIgnored || isMethodIgnored)
                            ctx.ReportDiagnostic(Diagnostic.Create(CsrfTokenDiagnosticAnalyzer.AuditRule, methodSymbol.Locations[0]));
                        else if (isArgumentIgnored)
                            ctx.ReportDiagnostic(Diagnostic.Create(CsrfTokenDiagnosticAnalyzer.FromBodyAuditRule, methodSymbol.Locations[0]));
                        else
                            ctx.ReportDiagnostic(Diagnostic.Create(CsrfTokenDiagnosticAnalyzer.Rule, methodSymbol.Locations[0]));
                    }
                }
            }

            private static bool IsClassIgnored(ITypeSymbol classSymbol, CsrfNamedGroup group)
            {
                if (!group.IgnoreAttributes.Any())
                    return false;

                return classSymbol.HasDerivedClassAttribute(c => IsIgnoreAttribute(c, group));
            }

            private static bool IsClassAnonymous(ITypeSymbol classSymbol, CsrfNamedGroup group)
            {
                if (!group.AnonymousAttributes.Any())
                    return false;

                return classSymbol.HasDerivedClassAttribute(c => IsAnonymousAttribute(c, group));
            }

            private static bool IsMethodIgnored(IMethodSymbol methodSymbol, CsrfNamedGroup group)
            {
                if (!group.IgnoreAttributes.Any())
                    return false;

                return methodSymbol.HasDerivedMethodAttribute(c => IsIgnoreAttribute(c, group));
            }

            private static bool IsMethodAction(IMethodSymbol methodSymbol, CsrfNamedGroup group)
            {
                if (!group.ActionAttributes.Any())
                    return false;

                return methodSymbol.HasDerivedMethodAttribute(c => IsActionAttribute(c, group));
            }

            private static bool IsArgumentIgnored(IMethodSymbol methodSymbol, ITypeSymbol classSymbol, CsrfNamedGroup group)
            {
                if (!group.IgnoreAttributes.Any())
                    return false;

                foreach (var parameter in methodSymbol.Parameters)
                {
                    if (parameter.HasAttribute(c => IsIgnoreAttribute(c, group)))
                        return true;
                }

                var isApiController = classSymbol.HasDerivedClassAttribute(c => IsIgnoreAttribute(c, group));
                return isApiController;
            }

            private static bool AntiforgeryAttributeExists(IMethodSymbol methodSymbol, CsrfNamedGroup group)
            {
                if (!group.AntiCsrfAttributes.Any())
                    return false;

                return methodSymbol.HasDerivedMethodAttribute(c => IsAntiForgeryToken(c, group));
            }
        }
    }
}
