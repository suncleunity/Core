﻿/*
 * This file is part of the CatLib package.
 *
 * (c) CatLib <support@catlib.io>
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 *
 * Document: https://catlib.io/
 */

using CatLib.Container;
using CatLib.EventDispatcher;
using CatLib.Exception;
using CatLib.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace CatLib
{
    /// <summary>
    /// The CatLib <see cref="Application"/> instance.
    /// </summary>
    public class Application : Container.Container, IApplication
    {
        private static string version;
        private readonly IList<IServiceProvider> loadedProviders;
        private readonly IList<Action<IApplication>> bootingCallbacks;
        private readonly IList<Action<IApplication>> bootedCallbacks;
        private readonly int mainThreadId;
        private readonly IDictionary<Type, string> dispatchMapping;
        private bool bootstrapped;
        private bool registering;
        private long incrementId;
        private DebugLevel debugLevel;
        private IEventDispatcher dispatcher;

        /// <summary>
        /// Initializes a new instance of the <see cref="Application"/> class.
        /// </summary>
        /// <param name="global">True if sets the instance to <see cref="App"/> facade.</param>
        public Application()
        {
            bootingCallbacks = new List<Action<IApplication>>();
            bootedCallbacks = new List<Action<IApplication>>();
            loadedProviders = new List<IServiceProvider>();

            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            RegisterBaseBindings();

            dispatchMapping = new Dictionary<Type, string>()
            {
                { typeof(AfterTerminateEventArgs), ApplicationEvents.OnAfterTerminate },
                { typeof(BeforeTerminateEventArgs), ApplicationEvents.OnBeforeTerminate },
            };

            // We use closures to save the current context state
            // Do not change to: OnFindType(Type.GetType) This
            // causes the active assembly to be not the expected scope.
            OnFindType(finder => { return Type.GetType(finder); });

            DebugLevel = DebugLevel.Production;
            Process = StartProcess.Construct;
        }

        /// <summary>
        /// Gets the CatLib <see cref="Application"/> version.
        /// </summary>
        public static string Version => version ?? (version = FileVersionInfo
                       .GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion);

        /// <summary>
        /// Gets indicates the application startup process.
        /// </summary>
        public StartProcess Process { get; private set; }

        /// <summary>
        /// Gets a value indicating whether if the application has booted.
        /// </summary>
        public bool IsBooted { get; private set; }

        /// <inheritdoc />
        public bool IsMainThread => mainThreadId == Thread.CurrentThread.ManagedThreadId;

        /// <inheritdoc />
        public DebugLevel DebugLevel
        {
            get => debugLevel;
            set
            {
                debugLevel = value;
                this.Instance<DebugLevel>(debugLevel);
            }
        }

        /// <inheritdoc cref="Application(bool)"/>
        /// <returns>The CatLib <see cref="Application"/> instance.</returns>
        public static Application New(bool global = true)
        {
            var application = new Application();
            if (global)
            {
                App.That = application;
            }

            return application;
        }

        /// <summary>
        /// Sets the event dispatcher.
        /// </summary>
        /// <param name="dispatcher">The event dispatcher instance.</param>
        public void SetDispatcher(IEventDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            this.Instance<IEventDispatcher>(dispatcher);
        }

        /// <inheritdoc />
        public IEventDispatcher GetDispatcher()
        {
            return dispatcher;
        }

        /// <summary>
        /// Register a new boot listener.
        /// </summary>
        public void Booting(Action<IApplication> callback)
        {
            bootingCallbacks.Add(callback);
        }

        /// <summary>
        /// Register a new "booted" listener.
        /// </summary>
        public void Booted(Action<IApplication> callback)
        {
            bootedCallbacks.Add(callback);

            if (IsBooted)
            {
                RaiseAppCallbacks(new[] { callback });
            }
        }

        /// <inheritdoc />
        public virtual void Terminate()
        {
            Process = StartProcess.Terminate;
            Raise(new BeforeTerminateEventArgs(this));
            Process = StartProcess.Terminating;
            Flush();
            if (App.That == this)
            {
                App.That = null;
            }

            Process = StartProcess.Terminated;
            Raise(new AfterTerminateEventArgs(this));
        }

        /// <summary>
        /// Bootstrap the given array of bootstrap classes.
        /// </summary>
        /// <param name="bootstraps">The given bootstrap classes.</param>
        public virtual void BootstrapWith(params IBootstrap[] bootstraps)
        {
            Guard.Requires<ArgumentNullException>(bootstraps != null);

            if (bootstrapped || Process != StartProcess.Construct)
            {
                throw new LogicException($"Cannot repeatedly trigger the {nameof(BootstrapWith)}()");
            }

            Process = StartProcess.Bootstrap;
            var existed = new HashSet<IBootstrap>();

            foreach (var bootstrap in bootstraps)
            {
                if (bootstrap == null)
                {
                    continue;
                }

                if (existed.Contains(bootstrap))
                {
                    throw new LogicException($"The bootstrap already exists : {bootstrap}");
                }

                existed.Add(bootstrap);
                bootstrap.Bootstrap();
            }

            bootstrapped = true;
        }

        /// <summary>
        /// Boot all of the registered service provider.
        /// </summary>
        public virtual void Boot()
        {
            if (!bootstrapped)
            {
                throw new LogicException($"You must call {nameof(BootstrapWith)}() first.");
            }

            if (IsBooted || Process != StartProcess.Bootstrap)
            {
                throw new LogicException($"Cannot repeatedly trigger the {nameof(Boot)}()");
            }

            Process = StartProcess.Boot;

            RaiseAppCallbacks(bootingCallbacks);

            foreach (var provider in loadedProviders)
            {
                BootProvider(provider);
            }

            IsBooted = true;
            Process = StartProcess.Running;

            RaiseAppCallbacks(bootedCallbacks);
        }

        /// <inheritdoc />
        public virtual void Register(IServiceProvider provider, bool force = false)
        {
            Guard.Requires<ArgumentNullException>(provider != null, $"Parameter \"{nameof(provider)}\" can not be null.");

            if (IsRegistered(provider))
            {
                if (!force)
                {
                    throw new LogicException($"Provider [{provider.GetType()}] is already register.");
                }

                loadedProviders.Remove(provider);
            }

            if (Process == StartProcess.Boot)
            {
                throw new LogicException($"Unable to add service provider during {nameof(StartProcess.Boot)}");
            }

            if (Process > StartProcess.Running)
            {
                throw new LogicException($"Unable to {nameof(Terminate)} in-process registration service provider");
            }

            if (provider is ServiceProvider baseProvider)
            {
                baseProvider.SetApplication(this);
            }

            try
            {
                registering = true;
                provider.Register();
            }
            finally
            {
                registering = false;
            }

            loadedProviders.Add(provider);

            if (IsBooted)
            {
                BootProvider(provider);
            }
        }

        /// <inheritdoc />
        public bool IsRegistered(IServiceProvider provider)
        {
            Guard.Requires<ArgumentNullException>(provider != null);
            return loadedProviders.Contains(provider);
        }

        /// <inheritdoc />
        public long GetRuntimeId()
        {
            return Interlocked.Increment(ref incrementId);
        }

        /// <summary>
        /// Boot the specified service provider.
        /// </summary>
        /// <param name="provider">The specified service provider.</param>
        protected virtual void BootProvider(IServiceProvider provider)
        {
            provider.Boot();
        }

        /// <inheritdoc />
        protected override void GuardConstruct(string method)
        {
            if (registering)
            {
                throw new LogicException(
                    $"It is not allowed to make services or dependency injection in the {nameof(Register)} process, method:{method}");
            }

            base.GuardConstruct(method);
        }

        private void RegisterBaseBindings()
        {
            this.Singleton<IApplication>(() => this).Alias<Application>().Alias<IContainer>();
            SetDispatcher(new EventDispatcher.EventDispatcher());
        }

        private void Raise<T>(T args)
            where T : EventArgs
        {
            if (!dispatchMapping.TryGetValue(args.GetType(), out string eventName))
            {
                throw new AssertException($"Assertion error: Undefined event {args}");
            }

            if (dispatcher == null)
            {
                return;
            }

            dispatcher.Raise(eventName, this, args);
        }

        private void RaiseAppCallbacks(IEnumerable<Action<IApplication>> callbacks)
        {
            foreach (var callback in callbacks)
            {
                callback(this);
            }
        }
    }
}
