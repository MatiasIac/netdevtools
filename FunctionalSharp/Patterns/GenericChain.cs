﻿using FunctionalSharp.Decorators;
using FunctionalSharp.Validators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunctionalSharp.Patterns
{

    public abstract class LinkBase<T>
    {
        public abstract void OnExecute(DataCargo<T> data);
    }

    public sealed class DataCargo<T>
    {
        public T Payload;
        public bool Cancel { get; set; }
    }

    public sealed class Configuration
    {
        public bool StopOnFailure { get; }
        public int RepeatTimesOnFailure { get; }

        public Configuration(
            bool stopOnFailure = true,
            int repeatTimesOnFailure = 0
        )
        {
            StopOnFailure = stopOnFailure;
            RepeatTimesOnFailure = repeatTimesOnFailure;
        }
    }

    public sealed class GenericChain<T>
    {
        private sealed class Link : LinkBase<T>
        {
            private readonly Action<DataCargo<T>> _action;

            public Link(Action<DataCargo<T>> action)
            {
                _action = action;
            }

            public override void OnExecute(DataCargo<T> data) => _action.Invoke(data);
        }

        private readonly DataCargo<T> _dataCargo;
        private readonly Configuration _configuration;
        private readonly List<LinkBase<T>> _chain;
        private Action<T> _completeAction;
        private Action<T, Exception> _errorAction;
        private Dictionary<string, LinkBase<T>> _decoratedLinkDictionary;

        internal GenericChain(T payload, Configuration configuration)
        {
            _dataCargo = new DataCargo<T>
            {
                Payload = GetPayloadOrInstance(payload)
            };

            _configuration = configuration ?? new Configuration();

            _chain = new List<LinkBase<T>>();
        }

        public static GenericChain<T> Create() => new GenericChain<T>(default, null);

        public static GenericChain<T> Create(Configuration configuration) => new GenericChain<T>(default, configuration);

        public static GenericChain<T> Create(T payload, Configuration configuration = null) => new GenericChain<T>(payload, configuration);

        public GenericChain<T> AddLink(Action<DataCargo<T>> action) => AddLink(new Link(action));

        public GenericChain<T> AddLink(LinkBase<T> link)
        {
            link.IfNull().Throw(new Exception("Chain Link cannot be null"));

            _chain.Add(link);

            return this;
        }

        public GenericChain<T> AddDecoratedLink(string linkName) => AddLink(GetLinkByDecorationName(linkName));

        #region Events
        public void Run()
        {
            bool failed = false;

            foreach (var link in _chain)
            {
                if (failed = RunLinkAndStop(link)) break;
            }

            if (!failed) _completeAction?.Invoke(_dataCargo.Payload);
        }

        /// <summary>
        /// After the chain is fully executed, OnCompleted is called
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        public GenericChain<T> OnCompleted(Action<T> action)
        {
            _completeAction = action;
            return this;
        }

        /// <summary>
        /// When any link of the chain throws an exception OnError is called
        /// </summary>
        /// <param name="action"></param>
        /// <returns>This chain</returns>
        public GenericChain<T> OnError(Action<T, Exception> action)
        {
            _errorAction = action;
            return this;
        }
        #endregion

        #region Privates
        private LinkBase<T> GetLinkByDecorationName(string name)
        {
            _decoratedLinkDictionary.IfNull()
                .Then(() => CreateDecoratedLinkDictionary());

            (_decoratedLinkDictionary.TryGetValue(name, out LinkBase<T> link))
                .IfFalse()
                .Throw(new Exception($"Decorated link {name} not found"));

            return link;
        }

        private void CreateDecoratedLinkDictionary()
        {
            //TODO: potential bug or innecessary iteration
            // if LinkBase<T>, concrete type differs from its T type
            // will be included into the dictionary as null
            // find a way to filter out null values from the main where
            _decoratedLinkDictionary = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                    assembly.GetTypes()
                        .Where(type => 
                            type.GetCustomAttributes(typeof(LinkAttribute), true).Count() > 0 
                            //&& type.IsAssignableFrom(typeof(LinkBase<T>))
                            ))
                .ToDictionary(k => 
                    (k.GetCustomAttributes(typeof(LinkAttribute), true)[0] as LinkAttribute).LinkName, 
                    v => Activator.CreateInstance(v) as LinkBase<T>);
        }

        private bool RunLinkAndStop(LinkBase<T> link, int attempt = 0)
        {
            try
            {
                _dataCargo.Cancel = false;
                link.OnExecute(_dataCargo);
                return _dataCargo.Cancel;
            }
            catch (Exception ex)
            {
                _errorAction?.Invoke(_dataCargo.Payload, ex);

                if (!_configuration.StopOnFailure && 
                    _configuration.RepeatTimesOnFailure > 0 &&
                    attempt < _configuration.RepeatTimesOnFailure - 1)
                {
                    return RunLinkAndStop(link, attempt + 1);
                }

                if (attempt == _configuration.RepeatTimesOnFailure) return true;

                return _configuration.StopOnFailure;
            }
        }

        private T GetPayloadOrInstance(T payload)
        {
            if (payload != null) return payload;

            var payloadType = typeof(T);

            if (payloadType.GetConstructor(Type.EmptyTypes) != null && !payloadType.IsAbstract)
            {
                return Activator.CreateInstance<T>();
            }

            throw new ArgumentException("Type must be creatable");
        }
        #endregion
    }
}