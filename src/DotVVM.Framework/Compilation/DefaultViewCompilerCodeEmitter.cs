using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotVVM.Framework.Binding;
using DotVVM.Framework.Runtime;
using DotVVM.Framework.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DotVVM.Framework.Compilation.ControlTree;
using System.Threading;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using DotVVM.Framework.Binding.Expressions;
using DotVVM.Framework.Compilation.Binding;
using Microsoft.Extensions.DependencyInjection;

namespace DotVVM.Framework.Compilation
{
    public class DefaultViewCompilerCodeEmitter
    {

        private int CurrentControlIndex;

        private List<StatementSyntax> CurrentStatements
        {
            get { return methods.Peek().Statements; }
        }

        public const string ControlBuilderFactoryParameterName = "controlBuilderFactory";
        public const string ServiceProviderParameterName = "services";
        public const string BuildControlFunctionName = nameof(IControlBuilder.BuildControl);
        public const string BuildTemplateFunctionName = "BuildTemplate";

        private Dictionary<GroupedDotvvmProperty, string> cachedGroupedDotvvmProperties = new Dictionary<GroupedDotvvmProperty, string>();
        private ConcurrentDictionary<(Type obj, string argTypes), string> injectionFactoryCache = new ConcurrentDictionary<(Type obj, string argTypes), string>();
        private Stack<EmitterMethodInfo> methods = new Stack<EmitterMethodInfo>();
        private List<EmitterMethodInfo> outputMethods = new List<EmitterMethodInfo>();
        public Type BuilderDataContextType { get; set; }
        public string ResultControlType { get; set; }

        private ConcurrentDictionary<Assembly, string> usedAssemblies = new ConcurrentDictionary<Assembly, string>();
        private static int assemblyIdCtr = 0;
        public IEnumerable<KeyValuePair<Assembly, string>> UsedAssemblies
        {
            get { return usedAssemblies; }
        }

        public string UseType(Type type)
        {
            if (type == null) return null;
            UseType(type.GetTypeInfo().BaseType);
            if (type.Assembly.FullName.Contains("System.Private.CoreLib"))
                return "global";
            return usedAssemblies.GetOrAdd(type.GetTypeInfo().Assembly, _ => "Asm_" + Interlocked.Increment(ref assemblyIdCtr));
        }

        protected List<MemberDeclarationSyntax> otherDeclarations = new List<MemberDeclarationSyntax>();

        public string EmitCreateVariable(ExpressionSyntax expression)
        {
            var name = "c" + CurrentControlIndex;
            CurrentControlIndex++;

            CurrentStatements.Add(
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")).WithVariables(
                        SyntaxFactory.VariableDeclarator(name).WithInitializer(
                            SyntaxFactory.EqualsValueClause(expression)
                        )
                    )
                )
            );
            return name;
        }

        /// <summary>
        /// Emits the create object expression.
        /// </summary>
        public string EmitCreateObject(Type type, object[] constructorArguments = null)
        {
            if (constructorArguments == null)
            {
                constructorArguments = new object[] { };
            }

            UseType(type);
            return EmitCreateObject(ParseTypeName(type), constructorArguments.Select(EmitValue));
        }

        public ExpressionSyntax InvokeDefaultInjectionFactory(Type objectType, Type[] parameterTypes) =>
            ParseTypeName(typeof(ActivatorUtilities))
            .Apply(a => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, a, SyntaxFactory.IdentifierName(nameof(ActivatorUtilities.CreateFactory))))
            .Apply(SyntaxFactory.InvocationExpression)
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(new[]
                {
                    EmitValue(objectType).Apply(SyntaxFactory.Argument),
                    EmitValue(parameterTypes).Apply(SyntaxFactory.Argument),
                })));


        public string EmitCustomInjectionFactoryInvocation(Type factoryType, Type controlType) =>
                SyntaxFactory.IdentifierName(ServiceProviderParameterName)
                .Apply(i => SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    i,
                    SyntaxFactory.IdentifierName(nameof(IServiceProvider.GetService))))
                .Apply(SyntaxFactory.InvocationExpression)
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument
                (EmitValue(factoryType)))))
                .Apply(n => SyntaxFactory.CastExpression(ParseTypeName(factoryType), n))
                .Apply(SyntaxFactory.InvocationExpression)
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ServiceProviderParameterName)),
                        SyntaxFactory.Argument(EmitValue(controlType))
                    })))
                .Apply(a => SyntaxFactory.CastExpression(ParseTypeName(controlType), a))
                .Apply(EmitCreateVariable);

        public string EmitInjectionFactoryInvocation(
            Type type,
            (Type type, ExpressionSyntax expression)[] arguments,
            Func<Type, Type[], ExpressionSyntax> factoryInvocation) =>
                this.injectionFactoryCache.GetOrAdd((type, string.Join(";", arguments.Select(i => i.type))), _ =>
                {
                    var fieldName = "Obj_" + type.Name + "_Factory_" + otherDeclarations.Count;
                    otherDeclarations.Add(SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                            this.ParseTypeName(typeof(ObjectFactory)))
                        .WithVariables(
                            SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(fieldName))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(
                                    factoryInvocation(type, arguments.Select(a => a.type).ToArray())
                                ))
                        )));
                    return fieldName;
                })
                .Apply(SyntaxFactory.IdentifierName)
                .Apply(SyntaxFactory.InvocationExpression)
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ServiceProviderParameterName)),
                        SyntaxFactory.Argument(SyntaxFactory.ArrayCreationExpression(
                            SyntaxFactory.ArrayType(ParseTypeName(typeof(object)))
                                .WithRankSpecifiers(SyntaxFactory.SingletonList<ArrayRankSpecifierSyntax>(SyntaxFactory.ArrayRankSpecifier(  SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.OmittedArraySizeExpression())))),
                            SyntaxFactory.InitializerExpression(SyntaxKind.ArrayInitializerExpression,
                                SyntaxFactory.SeparatedList(arguments.Select(a => a.expression)))))
                    })))
                .Apply(a => SyntaxFactory.CastExpression(ParseTypeName(type), a))
                .Apply(EmitCreateVariable);

        /// <summary>
        /// Emits the create object expression.
        /// </summary>
        public string EmitCreateObject(string typeName, object[] constructorArguments = null)
        {
            if (constructorArguments == null)
            {
                constructorArguments = new object[] { };
            }

            var typeSyntax = ReflectionUtils.IsFullName(typeName)
                ? ParseTypeName(ReflectionUtils.FindType(typeName))
                : SyntaxFactory.ParseTypeName(typeName);

            return EmitCreateObject(typeSyntax, constructorArguments.Select(EmitValue));
        }


        public string EmitCreateObject(TypeSyntax type, IEnumerable<ExpressionSyntax> arguments)
        {
            return EmitCreateVariable(
                EmitCreateObjectExpression(type, arguments)
            );
        }

        public ExpressionSyntax CreateObjectExpression(Type type, IEnumerable<ExpressionSyntax> arguments)
        {
            return EmitCreateObjectExpression(ParseTypeName(type), arguments);
        }

        public ExpressionSyntax EmitCreateObjectExpression(TypeSyntax type, IEnumerable<ExpressionSyntax> arguments)
        {
            return SyntaxFactory.ObjectCreationExpression(type).WithArgumentList(
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(
                        arguments.Select(SyntaxFactory.Argument)
                    )
                )
            );
        }

        public ExpressionSyntax EmitAttributeInitializer(CustomAttributeData attr)
        {
            UseType(attr.AttributeType);
            return SyntaxFactory.ObjectCreationExpression(
                ParseTypeName(attr.AttributeType),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(
                        attr.ConstructorArguments.Select(a => SyntaxFactory.Argument(EmitValue(a.Value)))
                    )
                ),
                SyntaxFactory.InitializerExpression(SyntaxKind.ObjectInitializerExpression,
                    SyntaxFactory.SeparatedList(
                        attr.NamedArguments.Select(np =>
                             (ExpressionSyntax)SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(np.MemberName),
                                EmitValue(np.TypedValue.Value)
                            )
                        )
                    )
                )
            );
        }

        /// <summary>
        /// Emits the control builder invocation.
        /// </summary>
        public string EmitInvokeControlBuilder(Type controlType, string virtualPath)
        {
            UseType(controlType);

            var builderName = "c" + CurrentControlIndex + "_builder";
            var untypedName = "c" + CurrentControlIndex + "_untyped";
            var name = "c" + CurrentControlIndex;
            CurrentControlIndex++;

            CurrentStatements.Add(
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")).WithVariables(
                        SyntaxFactory.VariableDeclarator(builderName).WithInitializer(
                            SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.InvocationExpression(
                                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                SyntaxFactory.IdentifierName(ControlBuilderFactoryParameterName),
                                                SyntaxFactory.IdentifierName(nameof(IControlBuilderFactory.GetControlBuilder))
                                            ),
                                            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                                                SyntaxFactory.Argument(EmitStringLiteral(virtualPath))
                                            }))
                                        ),
                                    SyntaxFactory.IdentifierName("Item2")),
                                SyntaxFactory.IdentifierName("Value"))
                            )
                        )
                    )
                )
            );
            CurrentStatements.Add(
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")).WithVariables(
                        SyntaxFactory.VariableDeclarator(untypedName).WithInitializer(
                            SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(builderName),
                                        SyntaxFactory.IdentifierName(nameof(IControlBuilder.BuildControl))
                                    ),
                                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ControlBuilderFactoryParameterName)),
                                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ServiceProviderParameterName))
                                    }))
                                )
                            )
                        )
                    )
                )
            );
            CurrentStatements.Add(
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")).WithVariables(
                        SyntaxFactory.VariableDeclarator(name).WithInitializer(
                            SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.CastExpression(ParseTypeName(controlType),
                                    SyntaxFactory.IdentifierName(untypedName)
                                )
                            )
                        )
                    )
                )
            );

            return name;
        }

        /// <summary>
        /// Emits the value.
        /// </summary>
        public ExpressionSyntax EmitValue(object value)
        {
            if (value == null)
            {
                return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            }
            if (value is string)
            {
                return EmitStringLiteral((string)value);
            }
            if (value is bool)
            {
                return EmitBooleanLiteral((bool)value);
            }
            if (value is int)
            {
                return EmitStandardNumericLiteral((int)value);
            }
            if (value is long)
            {
                return EmitStandardNumericLiteral((long)value);
            }
            if (value is ulong)
            {
                return EmitStandardNumericLiteral((ulong)value);
            }
            if (value is uint)
            {
                return EmitStandardNumericLiteral((uint)value);
            }
            if (value is decimal)
            {
                return EmitStandardNumericLiteral((decimal)value);
            }
            if (value is float)
            {
                return EmitStandardNumericLiteral((float)value);
            }
            if (value is double)
            {
                return EmitStandardNumericLiteral((double)value);
            }
            if (value is Type)
            {
                UseType(value as Type);
                return SyntaxFactory.TypeOfExpression(ParseTypeName((value as Type)));
            }

            var type = value.GetType();


            if (ReflectionUtils.IsNumericType(type))
            {
                return EmitStrangeIntegerValue(Convert.ToInt64(value), type);
            }

            if (type.GetTypeInfo().IsEnum)
            {
                UseType(type);
                var flags =
                    value.ToString().Split(',').Select(v =>
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            ParseTypeName(type),
                            SyntaxFactory.IdentifierName(v.Trim())
                        )
                   ).ToArray();
                ExpressionSyntax expr = flags[0];
                foreach (var i in flags.Skip(1))
                {
                    expr = SyntaxFactory.BinaryExpression(SyntaxKind.BitwiseOrExpression, expr, i);
                }
                return expr;
            }
            if (type.IsArray || typeof(IEnumerable).IsAssignableFrom(type))
            {
                return EmitCreateArray(ReflectionUtils.GetEnumerableType(type), (IEnumerable)value);
            }
            if (IsImmutableObject(type))
                return EmitValueReference(value);
            throw new NotSupportedException($"Emiting value of type '{value.GetType().FullName}' is not supported.");
        }

        public ExpressionSyntax EmitCreateArray(Type arrayType, IEnumerable values)
        {
            return SyntaxFactory.ArrayCreationExpression(
                                    SyntaxFactory.ArrayType(
                                        ParseTypeName(arrayType),
                                        SyntaxFactory.SingletonList(
                                            SyntaxFactory.ArrayRankSpecifier(
                                                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                                    SyntaxFactory.OmittedArraySizeExpression())))),
                                    SyntaxFactory.InitializerExpression(
                                        SyntaxKind.ArrayInitializerExpression,
                                        SyntaxFactory.SeparatedList(
                                            values.Cast<object>().Select(EmitValue))));
        }

        /// <summary>
        /// Emits the boolean literal.
        /// </summary>
        private ExpressionSyntax EmitBooleanLiteral(bool value)
        {
            return SyntaxFactory.LiteralExpression(value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression);
        }

        public ExpressionSyntax EmitValueReference(object value)
        {
            var id = AddObject(value);
            return SyntaxFactory.ElementAccessExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    ParseTypeName(typeof(DefaultViewCompilerCodeEmitter)),
                    SyntaxFactory.IdentifierName(nameof(_ViewImmutableObjects))),
                    SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(EmitValue(id)))));
        }

        /// <summary>
        /// Emits the set property statement.
        /// </summary>
        public void EmitSetProperty(string controlName, string propertyName, ExpressionSyntax valueSyntax)
        {
            CurrentStatements.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(controlName),
                            SyntaxFactory.IdentifierName(propertyName)
                        ),
                    valueSyntax
                    )
                )
            );
        }

        public ExpressionSyntax CreateDotvvmPropertyIdentifier(DotvvmProperty property)
        {
            if (property is GroupedDotvvmProperty gprop && gprop.PropertyGroup.DescriptorField != null)
            {
                string fieldName;
                if (!cachedGroupedDotvvmProperties.TryGetValue(gprop, out fieldName))
                {
                    fieldName = $"_staticCachedGroupProperty_{cachedGroupedDotvvmProperties.Count}";
                    cachedGroupedDotvvmProperties.Add(gprop, fieldName);
                    otherDeclarations.Add(SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(ParseTypeName(typeof(DotvvmProperty)),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(fieldName)
                                .WithInitializer(SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.ParseName(gprop.PropertyGroup.DeclaringType.FullName + "." + gprop.PropertyGroup.DescriptorField.Name
                                            + "." + nameof(DotvvmPropertyGroup.GetDotvvmProperty)),
                                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                                            SyntaxFactory.Argument(this.EmitStringLiteral(gprop.GroupMemberName))
                                        ))
                                    )
                                ))
                            )
                        )
                    ));
                }
                return SyntaxFactory.ParseName(fieldName);
            }
            else if (property.DeclaringType.GetField(property.Name + "Property", BindingFlags.Static | BindingFlags.Public) != null)
            {
                return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    ParseTypeName(property.DeclaringType),
                    SyntaxFactory.IdentifierName(property.Name + "Property"));
            }
            else
            {
                return SyntaxFactory.CastExpression(
                    this.ParseTypeName(typeof(DotvvmProperty)),
                    this.EmitValueReference(property));
            }
        }

        public void EmitSetDotvvmProperty(string controlName, DotvvmProperty property, object value) =>
            EmitSetDotvvmProperty(controlName, property, EmitValue(value));

        public void EmitSetDotvvmProperty(string controlName, DotvvmProperty property, ExpressionSyntax value)
        {
            UseType(property.DeclaringType);
            UseType(property.PropertyType);

            if (property.IsVirtual)
            {
                var gProperty = property as GroupedDotvvmProperty;
                if (gProperty != null && gProperty.PropertyGroup.PropertyGroupMode == PropertyGroupMode.ValueCollection)
                {
                    EmitAddToDictionary(controlName, property.CastTo<GroupedDotvvmProperty>().PropertyGroup.Name, gProperty.GroupMemberName, value);
                }
                else
                {
                    EmitSetProperty(controlName, property.PropertyInfo.Name, value);
                }
            }
            else
            {
                CurrentStatements.Add(
                  SyntaxFactory.ExpressionStatement(
                      SyntaxFactory.InvocationExpression(
                          SyntaxFactory.MemberAccessExpression(
                              SyntaxKind.SimpleMemberAccessExpression,
                              CreateDotvvmPropertyIdentifier(property),
                              SyntaxFactory.IdentifierName("SetValue")
                          ),
                          SyntaxFactory.ArgumentList(
                              SyntaxFactory.SeparatedList(
                                  new[] {
                                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(controlName)),
                                        SyntaxFactory.Argument(value)
                                  }
                              )
                          )
                      )
                  )
              );
            }
        }

        /// <summary>
        /// Emits the code that adds the specified value as a child item in the collection.
        /// </summary>
        public void EmitAddCollectionItem(string controlName, string variableName, string collectionPropertyName = "Children")
        {
            ExpressionSyntax collectionExpression;
            if (string.IsNullOrEmpty(collectionPropertyName))
            {
                collectionExpression = SyntaxFactory.IdentifierName(controlName);
            }
            else
            {
                collectionExpression = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(controlName),
                    SyntaxFactory.IdentifierName(collectionPropertyName)
                );
            }

            CurrentStatements.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            collectionExpression,
                            SyntaxFactory.IdentifierName("Add")
                        )
                    ).WithArgumentList(
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList(
                                new[]
                                {
                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(variableName))
                                }
                            )
                        )
                    )
                )
            );
        }

        /// <summary>
        /// Emits the add HTML attribute.
        /// </summary>
        public void EmitAddToDictionary(string controlName, string propertyName, string key, ExpressionSyntax valueSyntax)
        {
            CurrentStatements.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.ElementAccessExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(controlName),
                                SyntaxFactory.IdentifierName(propertyName)
                            ),
                            SyntaxFactory.BracketedArgumentList(
                                SyntaxFactory.SeparatedList(
                                    new[]
                                    {
                                        SyntaxFactory.Argument(EmitStringLiteral(key))
                                    }
                                )
                            )
                        ),
                        valueSyntax
                    )
                )
            );
        }

        private LiteralExpressionSyntax EmitStringLiteral(string value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));
        }

        private LiteralExpressionSyntax EmitStandardNumericLiteral(int value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
        }

        private LiteralExpressionSyntax EmitStandardNumericLiteral(long value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
        }

        private LiteralExpressionSyntax EmitStandardNumericLiteral(ulong value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
        }

        private LiteralExpressionSyntax EmitStandardNumericLiteral(uint value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
        }

        private LiteralExpressionSyntax EmitStandardNumericLiteral(decimal value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
        }

        private LiteralExpressionSyntax EmitStandardNumericLiteral(float value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
        }

        private LiteralExpressionSyntax EmitStandardNumericLiteral(double value)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
        }

        private ExpressionSyntax EmitStrangeIntegerValue(long value, Type type)
        {
            return SyntaxFactory.CastExpression(this.ParseTypeName(type), SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value)));
        }

        /// <summary>
        /// Emits the identifier.
        /// </summary>
        public NameSyntax EmitIdentifier(string identifier)
        {
            return SyntaxFactory.IdentifierName(identifier);
        }

        /// <summary>
        /// Emits the add directive.
        /// </summary>
        public void EmitAddDirective(string controlName, string name, string value)
        {
            CurrentStatements.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.ElementAccessExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(controlName),
                                SyntaxFactory.IdentifierName("Directives")
                            ),
                            SyntaxFactory.BracketedArgumentList(
                                SyntaxFactory.SeparatedList(
                                    new[]
                                    {
                                        SyntaxFactory.Argument(EmitStringLiteral(name))
                                    }
                                )
                            )
                        ),
                        EmitStringLiteral(value)
                    )
                )
            );
        }

        public string EmitEnsureCollectionInitialized(string parentName, DotvvmProperty property)
        {
            UseType(property.PropertyType);

            if (property.IsVirtual)
            {
                StatementSyntax initializer;
                if (property.PropertyInfo.SetMethod != null)
                {
                    initializer = SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName(parentName),
                                    SyntaxFactory.IdentifierName(property.Name)
                                ),
                                SyntaxFactory.ObjectCreationExpression(ParseTypeName(property.PropertyType))
                                    .WithArgumentList(
                                        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new ArgumentSyntax[] { }))
                                    )
                            )
                        );
                }
                else
                {
                    initializer = SyntaxFactory.ThrowStatement(
                        CreateObjectExpression(typeof(InvalidOperationException),
                            new[] { EmitStringLiteral($"Property '{ property.FullName }' can't be used as control collection since it is not initialized and does not have setter available for automatic initialization") }
                        )
                    );
                }
                CurrentStatements.Add(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.EqualsExpression,
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(parentName),
                                SyntaxFactory.IdentifierName(property.Name)
                            ),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                        ),
                        initializer
                    )
                );

                return EmitCreateVariable(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(parentName),
                        SyntaxFactory.IdentifierName(property.Name)
                    )
                );
            }
            else
            {
                CurrentStatements.Add(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.EqualsExpression,
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName(parentName),
                                    SyntaxFactory.IdentifierName("GetValue")
                                ),
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SeparatedList(new[]
                                    {
                                        SyntaxFactory.Argument(SyntaxFactory.ParseName(property.DescriptorFullName))
                                    })
                                )
                            ),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                        ),
                        SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName(parentName),
                                    SyntaxFactory.IdentifierName("SetValue")
                                ),
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SeparatedList(new[]
                                    {
                                        SyntaxFactory.Argument(SyntaxFactory.ParseName(property.DescriptorFullName)),
                                        SyntaxFactory.Argument(
                                            SyntaxFactory.ObjectCreationExpression(ParseTypeName(property.PropertyType))
                                                .WithArgumentList(
                                                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new ArgumentSyntax[] { }))
                                                )
                                        )
                                    })
                                )
                            )
                        )
                    )
                );
                return EmitCreateVariable(
                    SyntaxFactory.CastExpression(
                        ParseTypeName(property.PropertyType),
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(parentName),
                                SyntaxFactory.IdentifierName("GetValue")
                            ),
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SeparatedList(new[]
                                {
                                    SyntaxFactory.Argument(SyntaxFactory.ParseName(property.DescriptorFullName))
                                })
                            )
                        )
                    )
                );
            }
        }

        public TypeSyntax ParseTypeName(Type type)
        {
            var asmName = UseType(type);
            if (type == typeof(void))
            {
                return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
            }
            else if (type.IsArray)
            {
                var elementType = type.GetElementType();
                return SyntaxFactory.ArrayType(
                    ParseTypeName(elementType)
                )
                .WithRankSpecifiers(
                    SyntaxFactory.SingletonList<ArrayRankSpecifierSyntax>(
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression()
                            )
                        )
                    )
                );
            }
            else if (!type.GetTypeInfo().IsGenericType)
            {
                return SyntaxFactory.ParseTypeName($"{asmName}::{type.FullName.Replace('+', '.')}");
            }
            else
            {
                var fullName = type.GetGenericTypeDefinition().FullName;
                if (fullName.Contains("`"))
                {
                    fullName = fullName.Substring(0, fullName.IndexOf("`", StringComparison.Ordinal));
                }

                var parts = fullName.Split('.');
                NameSyntax identifier = SyntaxFactory.AliasQualifiedName(
                    SyntaxFactory.IdentifierName(asmName),
                    SyntaxFactory.IdentifierName(parts[0]));
                for (var i = 1; i < parts.Length - 1; i++)
                {
                    identifier = SyntaxFactory.QualifiedName(identifier, SyntaxFactory.IdentifierName(parts[i]));
                }

                var typeArguments = type.GetGenericArguments().Select(ParseTypeName);
                return SyntaxFactory.QualifiedName(identifier,
                    SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier(parts[parts.Length - 1]),
                        SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SeparatedList(typeArguments.ToArray())
                        )
                    )
                );
            }
        }

        public void EmitStatement(StatementSyntax statement) => CurrentStatements.Add(statement);

        /// <summary>
        /// Emits the return clause.
        /// </summary>
        public void EmitReturnClause(string variableName)
        {
            CurrentStatements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(variableName)));
        }

        protected virtual IEnumerable<BaseTypeSyntax> GetBuilderBaseTypes() => new BaseTypeSyntax[] {
            SyntaxFactory.SimpleBaseType(
                ParseTypeName(typeof(IControlBuilder))
            )
        };

        protected virtual IEnumerable<MemberDeclarationSyntax> GetDefaultMemberDeclarations() => new [] {
            SyntaxFactory.PropertyDeclaration(ParseTypeName(typeof(Type)), nameof(IControlBuilder.DataContextType))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithExpressionBody(
                    SyntaxFactory.ArrowExpressionClause(SyntaxFactory.TypeOfExpression(ParseTypeName(BuilderDataContextType))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory.PropertyDeclaration(ParseTypeName(typeof(Type)), nameof(IControlBuilder.ControlType))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithExpressionBody(
                    SyntaxFactory.ArrowExpressionClause(SyntaxFactory.TypeOfExpression(
                        ReflectionUtils.IsFullName(ResultControlType)
                            ? ParseTypeName(ReflectionUtils.FindType(ResultControlType))
                            : SyntaxFactory.ParseTypeName(ResultControlType))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
        };

        protected virtual ClassDeclarationSyntax ProcessViewBuilderClass(ClassDeclarationSyntax @class, string fileName) =>
            @class.AddAttributeLists(SyntaxFactory.AttributeList(SyntaxFactory.SeparatedList(new [] {
                SyntaxFactory.Attribute(
                    (QualifiedNameSyntax)ParseTypeName(typeof(LoadControlBuilderAttribute)),
                    SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(new [] {
                        SyntaxFactory.AttributeArgument(EmitStringLiteral(fileName))
                    }))
                )
            })));

        /// <summary>
        /// Gets the result syntax tree.
        /// </summary>
        public IEnumerable<SyntaxTree> BuildTree(string namespaceName, string className, string fileName)
        {
            UseType(BuilderDataContextType);

            var root = SyntaxFactory.CompilationUnit()
                .WithMembers(
                SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).WithMembers(
                    SyntaxFactory.List<MemberDeclarationSyntax>(
                        new[]
                        {
                            SyntaxFactory.ClassDeclaration(className)
                                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                                .WithBaseList(SyntaxFactory.BaseList(
                                    SyntaxFactory.SeparatedList(GetBuilderBaseTypes())
                                ))
                                .WithMembers(
                                SyntaxFactory.List<MemberDeclarationSyntax>(
                                    outputMethods.Select<EmitterMethodInfo, MemberDeclarationSyntax>(m =>
                                        SyntaxFactory.MethodDeclaration(
                                            m.ReturnType,
                                            m.Name)
                                            .WithModifiers(SyntaxFactory.TokenList(
                                                m.IsStatic ?
                                                new [] { SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword) } :
                                                m.IsOverride ?
                                                new [] { SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword) } :
                                                new [] { SyntaxFactory.Token(SyntaxKind.PublicKeyword) }
                                            ))
                                            .WithParameterList(m.Parameters)
                                            .WithBody(SyntaxFactory.Block(m.Statements))
                                        ).Concat(GetDefaultMemberDeclarations()).Concat(otherDeclarations)
                                    )
                                )
                                .Apply(c => ProcessViewBuilderClass(c, fileName))
                            }
                        )
                    )
                )
                .WithExterns(SyntaxFactory.List(
                    UsedAssemblies.Select(k => SyntaxFactory.ExternAliasDirective(SyntaxFactory.Identifier(k.Value)))
                ));

            // WORKAROUND: serializing and parsing the tree is necessary here because Roslyn throws compilation errors when pass the original tree which uses markup controls (they reference in-memory assemblies)
            // the trees are the same (root2.GetChanges(root) returns empty collection) but without serialization and parsing it does not work
            //SyntaxTree = CSharpSyntaxTree.ParseText(root.ToString());
            //SyntaxTree = root.SyntaxTree;
            return new[] { root.SyntaxTree };
        }


        public ParameterSyntax EmitParameter(string name, Type type)
        =>
            SyntaxFactory.Parameter(
                SyntaxFactory.Identifier(name)
            )
            .WithType(ParseTypeName(type));

        public ParameterSyntax[] EmitControlBuilderParameters()
            => new[]
            {
                EmitParameter(ControlBuilderFactoryParameterName, typeof(IControlBuilderFactory)),
                EmitParameter(ServiceProviderParameterName, typeof(IServiceProvider))
            };

        /// <summary>
        /// Pushes the new method.
        /// </summary>
        public void PushNewMethod(string name, Type returnType, params ParameterSyntax[] parameters)
        {
            var emitterMethodInfo = new EmitterMethodInfo(ParseTypeName(returnType), parameters) { Name = name };
            methods.Push(emitterMethodInfo);
        }

        /// <summary>
        /// Pushes the new method.
        /// </summary>
        public void PushNewOverrideMethod(string name, Type returnType, params ParameterSyntax[] parameters)
        {
            var emitterMethodInfo = new EmitterMethodInfo(ParseTypeName(returnType), parameters) { Name = name, IsOverride = true };
            methods.Push(emitterMethodInfo);
        }

        /// <summary>
        /// Pushes the new method.
        /// </summary>
        public void PushNewStaticMethod(string name, Type returnType, params ParameterSyntax[] parameters)
        {
            var emitterMethodInfo = new EmitterMethodInfo(ParseTypeName(returnType), parameters) { Name = name, IsStatic = true };
            methods.Push(emitterMethodInfo);
        }

        public ExpressionSyntax PopAsLambda(Type lambdaType = null)
        {
            var m = methods.Pop();
            var lambda = SyntaxFactory.ParenthesizedLambdaExpression(
                m.Parameters,
                SyntaxFactory.Block(m.Statements)
            );
            if (lambdaType == null)
                return lambda;
            else
                return SyntaxFactory.CastExpression(
                    ParseTypeName(lambdaType),
                    SyntaxFactory.ParenthesizedExpression(lambda));
        }

        /// <summary>
        /// Pops the method.
        /// </summary>
        public void PopMethod()
        {
            outputMethods.Add(methods.Pop());
        }


        /// <summary>
        /// Emits the control class.
        /// </summary>
        public void EmitControlClass(Type baseType, string className)
        {
            otherDeclarations.Add(
                SyntaxFactory.ClassDeclaration(className)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SeparatedList<BaseTypeSyntax>(new[]
                        {
                            SyntaxFactory.SimpleBaseType(ParseTypeName(baseType))
                        })
                    )
                )
            );
        }

        public static object[] _ViewImmutableObjects = new object[16];
        private static Func<Type, bool> IsImmutableObject = t => typeof(IBinding).IsAssignableFrom(t) || t == typeof(DataContextStack);
        private static int _viewObjectsCount = 0;
        public static int AddObject(object obj)
        {
            // Is there any ConcurrentList implementation? feel free to replace this 
            void resize(int minSize)
            {
                lock (_ViewImmutableObjects)
                {
                    if (minSize < _ViewImmutableObjects.Length) return;
                    var newArray = new object[_ViewImmutableObjects.Length * 2];
                    Array.Copy(_ViewImmutableObjects, 0, newArray, 0, _ViewImmutableObjects.Length);
                    // read/writes of references are atomic
                    _ViewImmutableObjects = newArray;
                }
            }
            var id = Interlocked.Increment(ref _viewObjectsCount);
            if (id >= _ViewImmutableObjects.Length) resize(id);
            _ViewImmutableObjects[id] = obj;
            return id;
        }
    }
}
