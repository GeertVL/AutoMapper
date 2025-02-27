namespace AutoMapper
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using Impl;
    using Internal;

    /// <summary>
    /// Main configuration object holding all mapping configuration for a source and destination type
    /// </summary>
    [DebuggerDisplay("{_sourceType.Type.Name} -> {_destinationType.Type.Name}")]
    public class TypeMap
    {
        private readonly IList<Action<object, object>> _afterMapActions = new List<Action<object, object>>();
        private readonly IList<Action<object, object>> _beforeMapActions = new List<Action<object, object>>();
        private readonly TypeInfo _destinationType;
        private readonly ISet<TypePair> _includedDerivedTypes = new HashSet<TypePair>();
        private readonly ThreadSafeList<PropertyMap> _propertyMaps = new ThreadSafeList<PropertyMap>();

        private readonly ThreadSafeList<SourceMemberConfig> _sourceMemberConfigs =
            new ThreadSafeList<SourceMemberConfig>();

        private readonly IList<PropertyMap> _inheritedMaps = new List<PropertyMap>();
        private PropertyMap[] _orderedPropertyMaps;
        private readonly TypeInfo _sourceType;
        private bool _sealed;
        private Func<ResolutionContext, bool> _condition;
        private int _maxDepth = Int32.MaxValue;

        public TypeMap(TypeInfo sourceType, TypeInfo destinationType, MemberList memberList)
        {
            _sourceType = sourceType;
            _destinationType = destinationType;
            Profile = ConfigurationStore.DefaultProfileName;
            ConfiguredMemberList = memberList;
        }

        public ConstructorMap ConstructorMap { get; private set; }

        public Type SourceType => _sourceType.Type;

        public Type DestinationType => _destinationType.Type;

        public string Profile { get; set; }
        public Func<ResolutionContext, object> CustomMapper { get; private set; }
        public LambdaExpression CustomProjection { get; private set; }

        public Action<object, object> BeforeMap
        {
            get
            {
                return (src, dest) =>
                {
                    foreach (var action in _beforeMapActions)
                        action(src, dest);
                };
            }
        }

        public Action<object, object> AfterMap
        {
            get
            {
                return (src, dest) =>
                {
                    foreach (var action in _afterMapActions)
                        action(src, dest);
                };
            }
        }

        public Func<ResolutionContext, object> DestinationCtor { get; set; }

        public List<string> IgnorePropertiesStartingWith { get; set; }

        public Type DestinationTypeOverride { get; set; }

        public bool ConstructDestinationUsingServiceLocator { get; set; }

        public MemberList ConfiguredMemberList { get; }

        public IEnumerable<TypePair> IncludedDerivedTypes => _includedDerivedTypes;

        public int MaxDepth
        {
            get { return _maxDepth; }
            set
            {
                _maxDepth = value;
                SetCondition(o => PassesDepthCheck(o, value));
            }
        }

        public Func<object, object> Substitution { get; set; }
        public LambdaExpression ConstructExpression { get; set; }

        public IEnumerable<PropertyMap> GetPropertyMaps()
        {
            return _sealed ? _orderedPropertyMaps : _propertyMaps.Concat(_inheritedMaps);
        }

        public void AddPropertyMap(PropertyMap propertyMap)
        {
            _propertyMaps.Add(propertyMap);
        }

        protected void AddInheritedMap(PropertyMap propertyMap)
        {
            _inheritedMaps.Add(propertyMap);
        }

        public void AddPropertyMap(IMemberAccessor destProperty, IEnumerable<IValueResolver> resolvers)
        {
            var propertyMap = new PropertyMap(destProperty);

            resolvers.Each(propertyMap.ChainResolver);

            AddPropertyMap(propertyMap);
        }

        public string[] GetUnmappedPropertyNames()
        {
            var autoMappedProperties = _propertyMaps.Where(pm => pm.IsMapped())
                .Select(pm => pm.DestinationProperty.Name);
            var inheritedProperties = _inheritedMaps.Where(pm => pm.IsMapped())
                .Select(pm => pm.DestinationProperty.Name);

            IEnumerable<string> properties;

            if (ConfiguredMemberList == MemberList.Destination)
                properties = _destinationType.PublicWriteAccessors
                    .Select(p => p.Name)
                    .Except(autoMappedProperties)
                    .Except(inheritedProperties);
            else
            {
                var redirectedSourceMembers = _propertyMaps
                    .Where(pm => pm.IsMapped())
                    .Where(pm => pm.CustomExpression != null)
                    .Where(pm => pm.SourceMember != null)
                    .Select(pm => pm.SourceMember.Name);

                var ignoredSourceMembers = _sourceMemberConfigs
                    .Where(smc => smc.IsIgnored())
                    .Select(pm => pm.SourceMember.Name);

                properties = _sourceType.PublicReadAccessors
                    .Select(p => p.Name)
                    .Except(autoMappedProperties)
                    .Except(inheritedProperties)
                    .Except(redirectedSourceMembers)
                    .Except(ignoredSourceMembers)
                    ;
            }

            return properties.Where(memberName => !IgnorePropertiesStartingWith.Any(memberName.StartsWith)).ToArray();
        }

        public PropertyMap FindOrCreatePropertyMapFor(IMemberAccessor destinationProperty)
        {
            var propertyMap = GetExistingPropertyMapFor(destinationProperty);

            if (propertyMap != null) return propertyMap;

            propertyMap = new PropertyMap(destinationProperty);

            AddPropertyMap(propertyMap);

            return propertyMap;
        }

        public void IncludeDerivedTypes(Type derivedSourceType, Type derivedDestinationType)
        {
            _includedDerivedTypes.Add(new TypePair(derivedSourceType, derivedDestinationType));
        }

        public Type GetDerivedTypeFor(Type derivedSourceType)
        {
            // This might need to be fixed for multiple derived source types to different dest types
            var match = _includedDerivedTypes.FirstOrDefault(tp => tp.SourceType == derivedSourceType);

            return match == null ? DestinationType : match.DestinationType;
        }

        public bool TypeHasBeenIncluded(Type derivedSourceType, Type derivedDestinationType)
        {
            return _includedDerivedTypes.Contains(new TypePair(derivedSourceType, derivedDestinationType));
        }

        public bool HasDerivedTypesToInclude()
        {
            return _includedDerivedTypes.Any();
        }

        public void UseCustomMapper(Func<ResolutionContext, object> customMapper)
        {
            CustomMapper = customMapper;
            _propertyMaps.Clear();
        }

        public void AddBeforeMapAction(Action<object, object> beforeMap)
        {
            _beforeMapActions.Add(beforeMap);
        }

        public void AddAfterMapAction(Action<object, object> afterMap)
        {
            _afterMapActions.Add(afterMap);
        }

        public void Seal()
        {
            if (_sealed)
                return;

            _orderedPropertyMaps =
                _propertyMaps
                    .Union(_inheritedMaps)
                    .OrderBy(map => map.GetMappingOrder()).ToArray();

            _orderedPropertyMaps.Each(pm => pm.Seal());

            _sealed = true;
        }

        public bool Equals(TypeMap other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(other._sourceType, _sourceType) && Equals(other._destinationType, _destinationType);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != typeof (TypeMap)) return false;
            return Equals((TypeMap) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (_sourceType.GetHashCode()*397) ^ _destinationType.GetHashCode();
            }
        }

        public PropertyMap GetExistingPropertyMapFor(IMemberAccessor destinationProperty)
        {
            var propertyMap =
                _propertyMaps.FirstOrDefault(pm => pm.DestinationProperty.Name.Equals(destinationProperty.Name));

            if (propertyMap != null)
                return propertyMap;

            propertyMap =
                _inheritedMaps.FirstOrDefault(pm => pm.DestinationProperty.Name.Equals(destinationProperty.Name));

            if (propertyMap == null)
                return null;

            var propertyInfo = propertyMap.DestinationProperty.MemberInfo as PropertyInfo;

            if (propertyInfo == null)
                return propertyMap;

            var baseAccessor = propertyInfo.GetAccessors()[0];

            if (baseAccessor.IsAbstract || baseAccessor.IsVirtual)
                return propertyMap;

            var accessor = ((PropertyInfo) destinationProperty.MemberInfo).GetAccessors()[0];

            if (baseAccessor.DeclaringType == accessor.DeclaringType)
                return propertyMap;

            return null;
        }

        public void AddInheritedPropertyMap(PropertyMap mappedProperty)
        {
            _inheritedMaps.Add(mappedProperty);
        }

        public void InheritTypes(TypeMap inheritedTypeMap)
        {
            foreach (var includedDerivedType in inheritedTypeMap._includedDerivedTypes
                .Where(includedDerivedType => !_includedDerivedTypes.Contains(includedDerivedType)))
            {
                _includedDerivedTypes.Add(includedDerivedType);
            }
        }

        public void SetCondition(Func<ResolutionContext, bool> condition)
        {
            _condition = condition;
        }

        public bool ShouldAssignValue(ResolutionContext resolutionContext)
        {
            return _condition == null || _condition(resolutionContext);
        }

        public void AddConstructorMap(ConstructorInfo constructorInfo, IEnumerable<ConstructorParameterMap> parameters)
        {
            var ctorMap = new ConstructorMap(constructorInfo, parameters);
            ConstructorMap = ctorMap;
        }

        public SourceMemberConfig FindOrCreateSourceMemberConfigFor(MemberInfo sourceMember)
        {
            var config = _sourceMemberConfigs.FirstOrDefault(smc => smc.SourceMember == sourceMember);
            if (config == null)
            {
                config = new SourceMemberConfig(sourceMember);
                _sourceMemberConfigs.Add(config);
            }

            return config;
        }

        private static bool PassesDepthCheck(ResolutionContext context, int maxDepth)
        {
            if (context.InstanceCache.ContainsKey(context))
            {
                // return true if we already mapped this value and it's in the cache
                return true;
            }

            ResolutionContext contextCopy = context;

            int currentDepth = 1;

            // walk parents to determine current depth
            while (contextCopy.Parent != null)
            {
                if (contextCopy.SourceType == context.TypeMap.SourceType &&
                    contextCopy.DestinationType == context.TypeMap.DestinationType)
                {
                    // same source and destination types appear higher up in the hierarchy
                    currentDepth++;
                }
                contextCopy = contextCopy.Parent;
            }
            return currentDepth <= maxDepth;
        }

        public void UseCustomProjection(LambdaExpression projectionExpression)
        {
            CustomProjection = projectionExpression;
            _propertyMaps.Clear();
        }

        public void ApplyInheritedMap(TypeMap inheritedTypeMap)
        {
            foreach (var inheritedMappedProperty in inheritedTypeMap.GetPropertyMaps().Where(m => m.IsMapped()))
            {
                var conventionPropertyMap = GetPropertyMaps()
                    .SingleOrDefault(m =>
                        m.DestinationProperty.Name == inheritedMappedProperty.DestinationProperty.Name);

                if (conventionPropertyMap != null && inheritedMappedProperty.HasCustomValueResolver)
                {
                    conventionPropertyMap.AssignCustomValueResolver(
                        inheritedMappedProperty.GetSourceValueResolvers().First());
                    conventionPropertyMap.AssignCustomExpression(inheritedMappedProperty.CustomExpression);
                }
                else if (conventionPropertyMap == null)
                {
                    var propertyMap = new PropertyMap(inheritedMappedProperty);

                    AddInheritedPropertyMap(propertyMap);
                }
            }

            //Include BeforeMap
            if (inheritedTypeMap.BeforeMap != null)
                AddBeforeMapAction(inheritedTypeMap.BeforeMap);
            //Include AfterMap
            if (inheritedTypeMap.AfterMap != null)
                AddAfterMapAction(inheritedTypeMap.AfterMap);
        }

        internal LambdaExpression DestinationConstructorExpression(Expression instanceParameter)
        {
            var ctorExpr = ConstructExpression;
            if(ctorExpr != null)
            {
                return ctorExpr;
            }
            Expression newExpression;
            if(ConstructorMap != null)
            {
                newExpression = ConstructorMap.NewExpression(instanceParameter);
            }
            else
            {
                newExpression = Expression.New(DestinationTypeOverride ?? DestinationType);
            }
            return Expression.Lambda(newExpression);
        }
    }
}