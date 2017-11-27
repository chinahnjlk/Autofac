// This software is part of the Autofac IoC container
// Copyright © 2011 Autofac Contributors
// http://autofac.org
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Features.Collections;
using Autofac.Features.GeneratedFactories;
using Autofac.Features.Indexed;
using Autofac.Features.LazyDependencies;
using Autofac.Features.Metadata;
using Autofac.Features.OwnedInstances;

namespace Autofac
{
    /// <summary>
    /// 用于从组件注册中构建一个IContainer.
    /// </summary>
    /// <example>
    /// <code>
    /// var builder = new ContainerBuilder();
    ///
    /// builder.RegisterType&lt;Logger&gt;()
    ///     .As&lt;ILogger&gt;()
    ///     .SingleInstance();
    ///
    /// builder.Register(c => new MessageHandler(c.Resolve&lt;ILogger&gt;()));
    ///
    /// var container = builder.Build();
    /// // resolve components from container...
    /// </code>
    /// </example>
    /// <remarks>Most <see cref="ContainerBuilder"/> functionality is accessed
    /// via extension methods in <see cref="RegistrationExtensions"/>.</remarks>
    /// <seealso cref="IContainer"/>
    /// <see cref="RegistrationExtensions"/>
    public class ContainerBuilder
    {
        private readonly IList<DeferredCallback> _configurationCallbacks = new List<DeferredCallback>();
        private bool _wasBuilt;

        private const string BuildCallbackPropertyKey = "__BuildCallbackKey";

        /// <summary>
        /// Initializes a new instance of the <see cref="ContainerBuilder"/> class.
        /// 初始化容器生成器类的新实例。
        /// </summary>
        public ContainerBuilder()
            : this(new Dictionary<string, object>())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ContainerBuilder"/> class.
        /// 初始化容器生成器类的新实例。
        /// </summary>
        /// <param name="properties">The properties used during component registration.</param>
        internal ContainerBuilder(IDictionary<string, object> properties)
        {
            Properties = properties;

            if (!Properties.ContainsKey(BuildCallbackPropertyKey))
            {
                Properties.Add(BuildCallbackPropertyKey, new List<Action<IContainer>>());
            }
        }

        /// <summary>
        ///  Gets 获取组件注册期间使用的属性集.
        /// </summary>
        /// <value>
        /// 在注册上下文可以共享的IDictionary
        /// </value>
        public IDictionary<string, object> Properties { get; }

        /// <summary>
        /// 注册一个在配置容器时调用的回调.
        /// </summary>
        /// <remarks>这主要是为了扩展生成器语法.</remarks>
        /// <param name="configurationCallback">Callback to execute.</param>
        public virtual DeferredCallback RegisterCallback(Action<IComponentRegistry> configurationCallback)
        {
            if (configurationCallback == null) throw new ArgumentNullException(nameof(configurationCallback));

            var c = new DeferredCallback(configurationCallback);
            _configurationCallbacks.Add(c);
            return c;
        }

        /// <summary>
        /// 注册一个在构建容器时调用的回调.
        /// </summary>
        /// <param name="buildCallback">Callback to execute.</param>
        /// <returns>The <see cref="ContainerBuilder"/> instance to continue registration calls.</returns>
        public ContainerBuilder RegisterBuildCallback(Action<IContainer> buildCallback)
        {
            if (buildCallback == null) throw new ArgumentNullException(nameof(buildCallback));

            var buildCallbacks = GetBuildCallbacks();
            buildCallbacks.Add(buildCallback);

            return this;
        }

        /// <summary>
        /// 创建一个包含已生成的组件注册的新容器.
        /// </summary>
        /// <param name="options">Options that influence the way the container is initialised.</param>
        /// <remarks>
        /// 一个带有配置组件注册的新容器
        /// </remarks>
        /// <returns>一个带有配置组件注册的新容器.</returns>
        public IContainer Build(ContainerBuildOptions options = ContainerBuildOptions.None)
        {
            var result = new Container(Properties);
            Build(result.ComponentRegistry, (options & ContainerBuildOptions.ExcludeDefaultModules) != ContainerBuildOptions.None);

            if ((options & ContainerBuildOptions.IgnoreStartableComponents) == ContainerBuildOptions.None)
                StartStartableComponents(result);

            var buildCallbacks = GetBuildCallbacks();
            foreach (var buildCallback in buildCallbacks)
                buildCallback(result);

            return result;
        }

        private static void StartStartableComponents(IComponentContext componentContext)
        {
            // 我们追踪哪些注册已经被添加了自动激活元数据的值。如果值存在，我们就不会重新激活。这可以帮助在容器更新的情况下。
            const string started = MetadataKeys.AutoActivated;
            object meta;

            foreach (var startable in componentContext.ComponentRegistry.RegistrationsFor(new TypedService(typeof(IStartable))).Where(r => !r.Metadata.TryGetValue(started, out meta)))
            {
                try
                {
                    var instance = (IStartable)componentContext.ResolveComponent(startable, Enumerable.Empty<Parameter>());
                    instance.Start();
                }
                finally
                {
                    startable.Metadata[started] = true;
                }
            }

            foreach (var registration in componentContext.ComponentRegistry.RegistrationsFor(new AutoActivateService()).Where(r => !r.Metadata.TryGetValue(started, out meta)))
            {
                try
                {
                    componentContext.ResolveComponent(registration, Enumerable.Empty<Parameter>());
                }
                catch (DependencyResolutionException ex)
                {
                    throw new DependencyResolutionException(String.Format(CultureInfo.CurrentCulture, ContainerBuilderResources.ErrorAutoActivating, registration), ex);
                }
                finally
                {
                    registration.Metadata[started] = true;
                }
            }
        }

        /// <summary>
        ///    用组件注册配置一个现有的容器所做.
        /// </summary>
        /// <remarks>
        /// 更新只能调用一次容器构建器
        /// -这可以防止提供实例的所有权问题.
        /// </remarks>
        /// <param name="container">注册的现有容器.</param>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "You can't update any arbitrary context, only containers.")]
        [Obsolete("Containers should generally be considered immutable. Register all of your dependencies before building/resolving. If you need to change the contents of a container, you technically should rebuild the container. This method may be removed in a future major release.")]
        public void Update(IContainer container)
        {
            Update(container, ContainerBuildOptions.None);
        }

        /// <summary>
        /// 用组件注册配置一个现有的容器
        /// 已经生成并允许指定额外的构建选项.
        /// </summary>
        /// <remarks>
        ///  更新只能调用一次容器构建器
        /// - 这可以防止提供实例的所有权问题.
        /// </remarks>
        /// <param name="container">注册的现有容器.</param>
        /// <param name="options">影响容器更新方式的选项.</param>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "You can't update any arbitrary context, only containers.")]
        [Obsolete("Containers should generally be considered immutable. Register all of your dependencies before building/resolving. If you need to change the contents of a container, you technically should rebuild the container. This method may be removed in a future major release.")]
        public void Update(IContainer container, ContainerBuildOptions options)
        {
            // Issue #462: 在这里添加ContainerBuildOptions参数作为重载而不是一个可选参数来避免方法绑定问题。在版本4.0或稍后我们应该将此重构为可选参数.
            if (container == null) throw new ArgumentNullException(nameof(container));
            Update(container.ComponentRegistry);
            if ((options & ContainerBuildOptions.IgnoreStartableComponents) == ContainerBuildOptions.None)
                StartStartableComponents(container);
        }

        /// <summary>
        /// 用组件注册配置一个现有的注册表
        /// 这已经被创造出来了.
        /// </summary>
        /// <remarks>
        /// 更新只能调用一次容器构建器
        /// -这可以防止提供实例的所有权问题。
        /// </remarks>
        /// <param name="componentRegistry">注册的注册中心.</param>
        [Obsolete("Containers should generally be considered immutable. Register all of your dependencies before building/resolving. If you need to change the contents of a container, you technically should rebuild the container. This method may be removed in a future major release.")]
        public void Update(IComponentRegistry componentRegistry)
        {
            this.UpdateRegistry(componentRegistry);
        }

        /// <summary>
        /// 用组件注册配置一个现有的注册表
        /// 所做的。主要用于动态添加注册
        /// 到一个子类的生命周期范围。
        /// </summary>
        /// <remarks>
        /// 更新只能调用一次容器构建器
        /// -这可以防止提供实例的所有权问题。
        /// </remarks>
        /// <param name="componentRegistry">注册的注册中心.</param>
        internal void UpdateRegistry(IComponentRegistry componentRegistry)
        {
            if (componentRegistry == null) throw new ArgumentNullException(nameof(componentRegistry));
            Build(componentRegistry, true);
        }

        private void Build(IComponentRegistry componentRegistry, bool excludeDefaultModules)
        {
            if (componentRegistry == null) throw new ArgumentNullException(nameof(componentRegistry));

            if (_wasBuilt)
                throw new InvalidOperationException(ContainerBuilderResources.BuildCanOnlyBeCalledOnce);

            _wasBuilt = true;

            if (!excludeDefaultModules)
                RegisterDefaultAdapters(componentRegistry);

            foreach (var callback in _configurationCallbacks)
                callback.Callback(componentRegistry);
        }

        private void RegisterDefaultAdapters(IComponentRegistry componentRegistry)
        {
            this.RegisterGeneric(typeof(KeyedServiceIndex<,>)).As(typeof(IIndex<,>)).InstancePerLifetimeScope();
            componentRegistry.AddRegistrationSource(new CollectionRegistrationSource());
            componentRegistry.AddRegistrationSource(new OwnedInstanceRegistrationSource());
            componentRegistry.AddRegistrationSource(new MetaRegistrationSource());
            componentRegistry.AddRegistrationSource(new LazyRegistrationSource());
            componentRegistry.AddRegistrationSource(new LazyWithMetadataRegistrationSource());
            componentRegistry.AddRegistrationSource(new StronglyTypedMetaRegistrationSource());
            componentRegistry.AddRegistrationSource(new GeneratedFactoryRegistrationSource());
        }

        private List<Action<IContainer>> GetBuildCallbacks()
        {
            return (List<Action<IContainer>>)Properties[BuildCallbackPropertyKey];
        }
    }
}