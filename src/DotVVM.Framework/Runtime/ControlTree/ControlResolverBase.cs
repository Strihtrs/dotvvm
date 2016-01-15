using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DotVVM.Framework.Binding;
using DotVVM.Framework.Configuration;
using DotVVM.Framework.Controls;
using DotVVM.Framework.Parser;
using DotVVM.Framework.Runtime.ControlTree.Resolved;

namespace DotVVM.Framework.Runtime.ControlTree
{
    /// <summary>
    /// Default DotVVM control resolver.
    /// </summary>
    public abstract class ControlResolverBase : IControlResolver
    {
        private readonly DotvvmConfiguration configuration;

        private ConcurrentDictionary<string, IControlType> cachedTagMappings = new ConcurrentDictionary<string, IControlType>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<IControlType, IControlResolverMetadata> cachedMetadata = new ConcurrentDictionary<IControlType, IControlResolverMetadata>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlResolverBase"/> class.
        /// </summary>
        public ControlResolverBase(DotvvmConfiguration configuration)
        {
            this.configuration = configuration;
        }

        /// <summary>
        /// Resolves the metadata for specified element.
        /// </summary>
        public virtual IControlResolverMetadata ResolveControl(string tagPrefix, string tagName, out object[] activationParameters)
        {
            // html element has no prefix
            if (string.IsNullOrEmpty(tagPrefix))
            {
                activationParameters = new object[] { tagName };
                return ResolveControl(new ResolvedTypeDescriptor(typeof(HtmlGenericControl)));
            }

            // find cached value
            var searchKey = GetSearchKey(tagPrefix, tagName);
            activationParameters = null;
            var controlType = cachedTagMappings.GetOrAdd(searchKey, _ => FindControlType(tagPrefix, tagName));
            var metadata = ResolveControl(controlType);
            return metadata;
        }

        private static string GetSearchKey(string tagPrefix, string tagName)
        {
            return tagPrefix + ":" + tagName;
        }

        /// <summary>
        /// Resolves the control metadata for specified type.
        /// </summary>
        public IControlResolverMetadata ResolveControl(IControlType controlType)
        {
            return cachedMetadata.GetOrAdd(controlType, _ => BuildControlMetadata(controlType));
        }

        /// <summary>
        /// Resolves the control metadata for specified type.
        /// </summary>
        public abstract IControlResolverMetadata ResolveControl(ITypeDescriptor controlType);



        public static Dictionary<string, BindingParserOptions> BindingTypes = new Dictionary<string, BindingParserOptions>(StringComparer.OrdinalIgnoreCase)
        {
            { Constants.ValueBinding, BindingParserOptions.Create<ValueBindingExpression>() },
            { Constants.CommandBinding, BindingParserOptions.Create<CommandBindingExpression>() },
            { Constants.ControlPropertyBinding, BindingParserOptions.Create<ControlPropertyBindingExpression>("_control") },
            { Constants.ControlCommandBinding, BindingParserOptions.Create<ControlCommandBindingExpression>("_control") },
            { Constants.ResourceBinding, BindingParserOptions.Create<ResourceBindingExpression>() },
            { Constants.StaticCommandBinding, BindingParserOptions.Create<StaticCommandBindingExpression>() },
        };

        /// <summary>
        /// Resolves the binding type.
        /// </summary>
        public virtual BindingParserOptions ResolveBinding(string bindingType)
        {
            BindingParserOptions bpo;
            if(BindingTypes.TryGetValue(bindingType, out bpo))
            {
                return bpo;
            }
            else
            {
                throw new NotSupportedException($"The binding {{{bindingType}: ... }} is unknown!");   // TODO: exception handling
            }
        }

        /// <summary>
        /// Finds the control metadata.
        /// </summary>
        protected virtual IControlType FindControlType(string tagPrefix, string tagName)
        {
            // try to match the tag prefix and tag name
            var rules = configuration.Markup.Controls.Where(r => r.IsMatch(tagPrefix, tagName));
            foreach (var rule in rules)
            {
                // validate the rule
                rule.Validate();

                if (string.IsNullOrEmpty(rule.TagName))
                {
                    // find the code only control
                    var compiledControl = FindCompiledControl(tagName, rule.Namespace, rule.Assembly);
                    if (compiledControl != null)
                    {
                        return compiledControl;
                    }
                }
                else
                {
                    // find the markup control
                    return FindMarkupControl(rule.Src);
                }
            }

            throw new Exception($"The control <{tagPrefix}:{tagName}> could not be resolved! Make sure that the tagPrefix is registered in DotvvmConfiguration.Markup.Controls collection!");
        }

        /// <summary>
        /// Finds the compiled control.
        /// </summary>
        protected abstract IControlType FindCompiledControl(string tagName, string namespaceName, string assemblyName);

        /// <summary>
        /// Finds the markup control.
        /// </summary>
        protected abstract IControlType FindMarkupControl(string file);

        /// <summary>
        /// Gets the control metadata.
        /// </summary>
        public abstract IControlResolverMetadata BuildControlMetadata(IControlType type);


    }
}