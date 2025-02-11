﻿#region Licence
/* The MIT License (MIT)
Copyright © 2014 Ian Cooper <ian_hammond_cooper@yahoo.co.uk>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Paramore.Brighter.FeatureSwitch;
using Paramore.Brighter.Logging;
using Polly;
using Polly.Registry;

namespace Paramore.Brighter
{
    /// <summary>
    /// Class CommandProcessor.
    /// Implements both the <a href="http://www.hillside.net/plop/plop2001/accepted_submissions/PLoP2001/bdupireandebfernandez0/PLoP2001_bdupireandebfernandez0_1.pdf">Command Dispatcher</a> 
    /// and <a href="http://wiki.hsr.ch/APF/files/CommandProcessor.pdf">Command Processor</a> Design Patterns 
    /// </summary>
    public class CommandProcessor : IAmACommandProcessor
    {
        private static readonly ILogger s_logger = ApplicationLogging.CreateLogger<CommandProcessor>();

        private readonly IAmAMessageMapperRegistry _mapperRegistry;
        private readonly IAmASubscriberRegistry _subscriberRegistry;
        private readonly IAmAHandlerFactorySync _handlerFactorySync;
        private readonly IAmAHandlerFactoryAsync _handlerFactoryAsync;
        private readonly IAmARequestContextFactory _requestContextFactory;
        private readonly IPolicyRegistry<string> _policyRegistry;
        private readonly InboxConfiguration _inboxConfiguration;
        private readonly IAmABoxTransactionConnectionProvider _boxTransactionConnectionProvider;
        private readonly IAmAFeatureSwitchRegistry _featureSwitchRegistry;
        private readonly IEnumerable<Subscription> _replySubscriptions;

        //Uses -1 to indicate no outbox and will thus force a throw on a failed publish

        // the following are not readonly to allow setting them to null on dispose
        private readonly IAmAChannelFactory _responseChannelFactory;

        
        /// <summary>
        /// Use this as an identifier for your <see cref="Policy"/> that determines for how long to break the circuit when communication with the Work Queue fails.
        /// Register that policy with your <see cref="IPolicyRegistry{TKey}"/> such as <see cref="PolicyRegistry"/>
        /// You can use this an identifier for you own policies, if your generic policy is the same as your Work Queue policy.
        /// </summary>
        public const string CIRCUITBREAKER = "Paramore.Brighter.CommandProcessor.CircuitBreaker";

        /// <summary>
        /// Use this as an identifier for your <see cref="Policy"/> that determines the retry strategy when communication with the Work Queue fails.
        /// Register that policy with your <see cref="IAmAPolicyRegistry"/> such as <see cref="PolicyRegistry"/>
        /// You can use this an identifier for you own policies, if your generic policy is the same as your Work Queue policy.
        /// </summary>
        public const string RETRYPOLICY = "Paramore.Brighter.CommandProcessor.RetryPolicy";

        /// <summary>
        /// Use this as an identifier for your <see cref="Policy"/> that determines for how long to break the circuit when communication with the Work Queue fails.
        /// Register that policy with your <see cref="IPolicyRegistry{TKey}"/> such as <see cref="PolicyRegistry"/>
        /// You can use this an identifier for you own policies, if your generic policy is the same as your Work Queue policy.
        /// </summary>
        public const string CIRCUITBREAKERASYNC = "Paramore.Brighter.CommandProcessor.CircuitBreaker.Async";

        /// <summary>
        /// Use this as an identifier for your <see cref="Policy"/> that determines the retry strategy when communication with the Work Queue fails.
        /// Register that policy with your <see cref="IPolicyRegistry{TKey}"/> such as <see cref="PolicyRegistry"/>
        /// You can use this an identifier for you own policies, if your generic policy is the same as your Work Queue policy.
        /// </summary>
        public const string RETRYPOLICYASYNC = "Paramore.Brighter.CommandProcessor.RetryPolicy.Async";
        
        //We want to use double lock to let us pass parameters to the constructor from the first instance
        private static ExternalBusServices _bus = null;
        private static readonly object padlock = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
        /// Use this constructor when no external bus is required and only sync handlers are needed
        /// </summary>
        /// <param name="subscriberRegistry">The subscriber registry.</param>
        /// <param name="handlerFactory">The handler factory.</param>
        /// <param name="requestContextFactory">The request context factory.</param>
        /// <param name="policyRegistry">The policy registry.</param>
        /// <param name="featureSwitchRegistry">The feature switch config provider.</param>
        /// <param name="inboxConfiguration">Do we want to insert an inbox handler into pipelines without the attribute. Null (default = no), yes = how to configure</param>
        public CommandProcessor(
            IAmASubscriberRegistry subscriberRegistry,
            IAmAHandlerFactory handlerFactory,
            IAmARequestContextFactory requestContextFactory,
            IPolicyRegistry<string> policyRegistry,
            IAmAFeatureSwitchRegistry featureSwitchRegistry = null,
            InboxConfiguration inboxConfiguration = null
        )
        {
            _subscriberRegistry = subscriberRegistry;
            if (handlerFactory is IAmAHandlerFactorySync handlerFactorySync)
                _handlerFactorySync = handlerFactorySync;
            if (handlerFactory is IAmAHandlerFactoryAsync handlerFactoryAsync)
                _handlerFactoryAsync = handlerFactoryAsync;
            _requestContextFactory = requestContextFactory;
            _policyRegistry = policyRegistry;
            _featureSwitchRegistry = featureSwitchRegistry;
            _inboxConfiguration = inboxConfiguration;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
        /// Use this constructor when only posting messages to an external bus is required
        /// </summary>
        /// <param name="requestContextFactory">The request context factory.</param>
        /// <param name="policyRegistry">The policy registry.</param>
        /// <param name="mapperRegistry">The mapper registry.</param>
        /// <param name="outBox">The outbox.</param>
        /// <param name="messageProducer">The messaging gateway.</param>
        /// <param name="outboxTimeout">How long should we wait to write to the outbox</param>
        /// <param name="featureSwitchRegistry">The feature switch config provider.</param>
        /// <param name="inboxConfiguration">Do we want to insert an inbox handler into pipelines without the attribute. Null (default = no), yes = how to configure</param>
        /// <param name="boxTransactionConnectionProvider">The Box Connection Provider to use when Depositing into the outbox.</param>
        public CommandProcessor(
            IAmARequestContextFactory requestContextFactory,
            IPolicyRegistry<string> policyRegistry,
            IAmAMessageMapperRegistry mapperRegistry,
            IAmAnOutbox<Message> outBox,
            IAmAMessageProducer messageProducer,
            int outboxTimeout = 300,
            IAmAFeatureSwitchRegistry featureSwitchRegistry = null,
            InboxConfiguration inboxConfiguration = null,
            IAmABoxTransactionConnectionProvider boxTransactionConnectionProvider = null)
        {
            _requestContextFactory = requestContextFactory;
            _policyRegistry = policyRegistry;
            _mapperRegistry = mapperRegistry;
            _featureSwitchRegistry = featureSwitchRegistry;
            _inboxConfiguration = inboxConfiguration;
            _boxTransactionConnectionProvider = boxTransactionConnectionProvider;

            InitExtServiceBus(policyRegistry, outBox, outboxTimeout, messageProducer);

            _bus.ConfigurePublisherCallbackMaybe();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
        /// Use this constructor when both rpc support is required
        /// </summary>
        /// <param name="subscriberRegistry">The subscriber registry.</param>
        /// <param name="handlerFactory">The handler factory.</param>
        /// <param name="requestContextFactory">The request context factory.</param>
        /// <param name="policyRegistry">The policy registry.</param>
        /// <param name="mapperRegistry">The mapper registry.</param>
        /// <param name="outBox">The outbox</param>
        /// <param name="messageProducer">The messaging gateway.</param>
        /// <param name="replySubscriptions">The Subscriptions for creating the reply queues</param>
        /// <param name="responseChannelFactory">If we are expecting a response, then we need a channel to listen on</param>
        /// <param name="outboxTimeout">How long should we wait to write to the outbox</param>
        /// <param name="featureSwitchRegistry">The feature switch config provider.</param>
        /// <param name="inboxConfiguration">Do we want to insert an inbox handler into pipelines without the attribute. Null (default = no), yes = how to configure</param>
        /// <param name="boxTransactionConnectionProvider">The Box Connection Provider to use when Depositing into the outbox.</param>
        public CommandProcessor(
            IAmASubscriberRegistry subscriberRegistry,
            IAmAHandlerFactory handlerFactory,
            IAmARequestContextFactory requestContextFactory,
            IPolicyRegistry<string> policyRegistry,
            IAmAMessageMapperRegistry mapperRegistry,
            IAmAnOutbox<Message> outBox,
            IAmAMessageProducer messageProducer,
            IEnumerable<Subscription> replySubscriptions,
            int outboxTimeout = 300,
            IAmAFeatureSwitchRegistry featureSwitchRegistry = null,
            IAmAChannelFactory responseChannelFactory = null,
            InboxConfiguration inboxConfiguration = null,
            IAmABoxTransactionConnectionProvider boxTransactionConnectionProvider = null)
            : this(subscriberRegistry, handlerFactory, requestContextFactory, policyRegistry)
        {
            _mapperRegistry = mapperRegistry;
            _featureSwitchRegistry = featureSwitchRegistry;
            _responseChannelFactory = responseChannelFactory;
            _inboxConfiguration = inboxConfiguration;
            _boxTransactionConnectionProvider = boxTransactionConnectionProvider;
            _replySubscriptions = replySubscriptions;

            InitExtServiceBus(policyRegistry, outBox, outboxTimeout, messageProducer);

            _bus.ConfigurePublisherCallbackMaybe();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
        /// Use this constructor when both external bus and command processor support is required 
        /// </summary>
        /// <param name="subscriberRegistry">The subscriber registry.</param>
        /// <param name="handlerFactory">The handler factory.</param>
        /// <param name="requestContextFactory">The request context factory.</param>
        /// <param name="policyRegistry">The policy registry.</param>
        /// <param name="mapperRegistry">The mapper registry.</param>
        /// <param name="outBox">The outbox.</param>
        /// <param name="messageProducer">The messaging gateway.</param>
        /// <param name="outboxTimeout">How long should we wait to write to the outbox</param>
        /// <param name="featureSwitchRegistry">The feature switch config provider.</param>
        /// <param name="inboxConfiguration">Do we want to insert an inbox handler into pipelines without the attribute. Null (default = no), yes = how to configure</param>
        /// <param name="boxTransactionConnectionProvider">The Box Connection Provider to use when Depositing into the outbox.</param>
        public CommandProcessor(
            IAmASubscriberRegistry subscriberRegistry,
            IAmAHandlerFactory handlerFactory,
            IAmARequestContextFactory requestContextFactory,
            IPolicyRegistry<string> policyRegistry,
            IAmAMessageMapperRegistry mapperRegistry,
            IAmAnOutbox<Message> outBox,
            IAmAMessageProducer messageProducer,
            int outboxTimeout = 300,
            IAmAFeatureSwitchRegistry featureSwitchRegistry = null,
            InboxConfiguration inboxConfiguration = null,
            IAmABoxTransactionConnectionProvider boxTransactionConnectionProvider = null)
            : this(subscriberRegistry, handlerFactory, requestContextFactory, policyRegistry, featureSwitchRegistry)
        {
            _mapperRegistry = mapperRegistry;
            _inboxConfiguration = inboxConfiguration;
            _boxTransactionConnectionProvider = boxTransactionConnectionProvider;

            InitExtServiceBus(policyRegistry, outBox, outboxTimeout, messageProducer);

            //Only register one, to avoid two callbacks where we support both interfaces on a producer
            if (!_bus.ConfigurePublisherCallbackMaybe()) _bus.ConfigureAsyncPublisherCallbackMaybe();
        }

        /// <summary>
        /// Sends the specified command. We expect only one handler. The command is handled synchronously.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="command">The command.</param>
        /// <exception cref="System.ArgumentException">
        /// </exception>
        public void Send<T>(T command) where T : class, IRequest
        {
            if (_handlerFactorySync == null)
                throw new InvalidOperationException("No handler factory defined.");

            var requestContext = _requestContextFactory.Create();
            requestContext.Policies = _policyRegistry;
            requestContext.FeatureSwitches = _featureSwitchRegistry;

            using (var builder = new PipelineBuilder<T>(_subscriberRegistry, _handlerFactorySync, _inboxConfiguration))
            {
                s_logger.LogInformation("Building send pipeline for command: {CommandType} {Id}", command.GetType(), command.Id);
                var handlerChain = builder.Build(requestContext);

                AssertValidSendPipeline(command, handlerChain.Count());

                handlerChain.First().Handle(command);
            }
        }

        /// <summary>
        /// Awaitably sends the specified command.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="command">The command.</param>
        /// <param name="continueOnCapturedContext">Should we use the calling thread's synchronization context when continuing or a default thread synchronization context. Defaults to false</param>
        /// <param name="cancellationToken">Allows the sender to cancel the request pipeline. Optional</param>
        /// <returns>awaitable <see cref="Task"/>.</returns>
        public async Task SendAsync<T>(T command, bool continueOnCapturedContext = false, CancellationToken cancellationToken = default(CancellationToken))
            where T : class, IRequest
        {
            if (_handlerFactoryAsync == null)
                throw new InvalidOperationException("No async handler factory defined.");

            var requestContext = _requestContextFactory.Create();
            requestContext.Policies = _policyRegistry;
            requestContext.FeatureSwitches = _featureSwitchRegistry;

            using (var builder = new PipelineBuilder<T>(_subscriberRegistry, _handlerFactoryAsync, _inboxConfiguration))
            {
                s_logger.LogInformation("Building send async pipeline for command: {CommandType} {Id}", command.GetType(), command.Id);
                var handlerChain = builder.BuildAsync(requestContext, continueOnCapturedContext);

                AssertValidSendPipeline(command, handlerChain.Count());

                await handlerChain.First().HandleAsync(command, cancellationToken).ConfigureAwait(continueOnCapturedContext);
            }
        }

        /// <summary>
        /// Publishes the specified event. We expect zero or more handlers. The events are handled synchronously, in turn
        /// Because any pipeline might throw, yet we want to execute the remaining handler chains,  we catch exceptions on any publisher
        /// instead of stopping at the first failure and then we throw an AggregateException if any of the handlers failed, 
        /// with the InnerExceptions property containing the failures.
        /// It is up the implementer of the handler that throws to take steps to make it easy to identify the handler that threw.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="event">The event.</param>
        public void Publish<T>(T @event) where T : class, IRequest
        {
            if (_handlerFactorySync == null)
                throw new InvalidOperationException("No handler factory defined.");

            var requestContext = _requestContextFactory.Create();
            requestContext.Policies = _policyRegistry;
            requestContext.FeatureSwitches = _featureSwitchRegistry;

            using (var builder = new PipelineBuilder<T>(_subscriberRegistry, _handlerFactorySync, _inboxConfiguration))
            {
                s_logger.LogInformation("Building send pipeline for event: {EventType} {Id}", @event.GetType(), @event.Id);
                var handlerChain = builder.Build(requestContext);

                var handlerCount = handlerChain.Count();

                s_logger.LogInformation("Found {HandlerCount} pipelines for event: {EventType} {Id}", handlerCount, @event.GetType(), @event.Id);

                var exceptions = new List<Exception>();
                foreach (var handleRequests in handlerChain)
                {
                    try
                    {
                        handleRequests.Handle(@event);
                    }
                    catch (Exception e)
                    {
                        exceptions.Add(e);
                    }
                }

                if (exceptions.Any())
                {
                    throw new AggregateException("Failed to publish to one more handlers successfully, see inner exceptions for details", exceptions);
                }
            }
        }

        /// <summary>
        /// Publishes the specified event with async/await. We expect zero or more handlers. The events are handled synchronously and concurrently
        /// Because any pipeline might throw, yet we want to execute the remaining handler chains,  we catch exceptions on any publisher
        /// instead of stopping at the first failure and then we throw an AggregateException if any of the handlers failed, 
        /// with the InnerExceptions property containing the failures.
        /// It is up the implementer of the handler that throws to take steps to make it easy to identify the handler that threw.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="event">The event.</param>
        /// <param name="continueOnCapturedContext">Should we use the calling thread's synchronization context when continuing or a default thread synchronization context. Defaults to false</param>
        /// <param name="cancellationToken">Allows the sender to cancel the request pipeline. Optional</param>
        /// <returns>awaitable <see cref="Task"/>.</returns>
        public async Task PublishAsync<T>(T @event, bool continueOnCapturedContext = false, CancellationToken cancellationToken = default(CancellationToken))
            where T : class, IRequest
        {
            if (_handlerFactoryAsync == null)
                throw new InvalidOperationException("No async handler factory defined.");

            var requestContext = _requestContextFactory.Create();
            requestContext.Policies = _policyRegistry;
            requestContext.FeatureSwitches = _featureSwitchRegistry;

            using (var builder = new PipelineBuilder<T>(_subscriberRegistry, _handlerFactoryAsync, _inboxConfiguration))
            {
                s_logger.LogInformation("Building send async pipeline for event: {EventType} {Id}", @event.GetType(), @event.Id);

                var handlerChain = builder.BuildAsync(requestContext, continueOnCapturedContext);
                var handlerCount = handlerChain.Count();

                s_logger.LogInformation("Found {0} async pipelines for event: {EventType} {Id}", handlerCount, @event.GetType(), @event.Id);

                var exceptions = new List<Exception>();
                foreach (var handler in handlerChain)
                {
                    try
                    {
                        await handler.HandleAsync(@event, cancellationToken).ConfigureAwait(continueOnCapturedContext);
                    }
                    catch (Exception e)
                    {
                        exceptions.Add(e);
                    }
                }

                if (exceptions.Count > 0)
                {
                    throw new AggregateException("Failed to async publish to one more handlers successfully, see inner exceptions for details", exceptions);
                }
            }
        }

        /// <summary>
        /// Posts the specified request. The message is placed on a task queue and into a outbox for reposting in the event of failure.
        /// You will need to configure a service that reads from the task queue to process the message
        /// Paramore.Brighter.ServiceActivator provides an endpoint for use in a windows service that reads from a queue
        /// and then Sends or Publishes the message to a <see cref="CommandProcessor"/> within that service. The decision to <see cref="Send{T}"/> or <see cref="Publish{T}"/> is based on the
        /// mapper. Your mapper can map to a <see cref="Message"/> with either a <see cref="T:MessageType.MT_COMMAND"/> , which results in a <see cref="Send{T}(T)"/> or a
        /// <see cref="T:MessageType.MT_EVENT"/> which results in a <see cref="Publish{T}(T)"/>
        /// Please note that this call will not participate in any ambient Transactions, if you wish to have the outbox participate in a Transaction please Use Deposit,
        /// and then after you have committed your transaction use ClearOutbox
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="request">The request.</param>
        /// <exception cref="System.ArgumentOutOfRangeException"></exception>
        public void Post<T>(T request) where T : class, IRequest
        {
            ClearOutbox(DepositPost(request, null));
        }

        /// <summary>
        /// Posts the specified request with async/await support. The message is placed on a task queue and into a outbox for reposting in the event of failure.
        /// You will need to configure a service that reads from the task queue to process the message
        /// Paramore.Brighter.ServiceActivator provides an endpoint for use in a windows service that reads from a queue
        /// and then Sends or Publishes the message to a <see cref="CommandProcessor"/> within that service. The decision to <see cref="Send{T}"/> or <see cref="Publish{T}"/> is based on the
        /// mapper. Your mapper can map to a <see cref="Message"/> with either a <see cref="T:MessageType.MT_COMMAND"/> , which results in a <see cref="Send{T}(T)"/> or a
        /// <see cref="T:MessageType.MT_EVENT"/> which results in a <see cref="Publish{T}(T)"/>
        /// Please note that this call will not participate in any ambient Transactions, if you wish to have the outbox participate in a Transaction please Use DepositAsync,
        /// and then after you have committed your transaction use ClearOutboxAsync
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="request">The request.</param>
        /// <param name="continueOnCapturedContext">Should we use the calling thread's synchronization context when continuing or a default thread synchronization context. Defaults to false</param>
        /// <param name="cancellationToken">Allows the sender to cancel the request pipeline. Optional</param>
        /// <exception cref="System.ArgumentOutOfRangeException"></exception>
        /// <returns>awaitable <see cref="Task"/>.</returns>
        public async Task PostAsync<T>(T request, bool continueOnCapturedContext = false, CancellationToken cancellationToken = default(CancellationToken))
            where T : class, IRequest
        {
            var messageId = await DepositPostAsync(request, null, continueOnCapturedContext, cancellationToken);
            await ClearOutboxAsync(new Guid[] {messageId}, continueOnCapturedContext, cancellationToken);
        }

        /// <summary>
        /// Adds a message into the outbox, and returns the id of the saved message.
        /// Intended for use with the Outbox pattern: http://gistlabs.com/2014/05/the-outbox/ normally you include the
        /// call to DepositPostBox within the scope of the transaction to write corresponding entity state to your
        /// database, that you want to signal via the request to downstream consumers
        /// Pass deposited Guid to <see cref="ClearOutbox"/> 
        /// </summary>
        /// <param name="request">The request to save to the outbox</param>
        /// <typeparam name="T">The type of the request</typeparam>
        /// <returns>The Id of the Message that has been deposited.</returns>
        public Guid DepositPost<T>(T request) where T : class, IRequest
        {
            return DepositPost(request, _boxTransactionConnectionProvider);
        }
        
        private Guid DepositPost<T>(T request, IAmABoxTransactionConnectionProvider connectionProvider) where T : class, IRequest
        {
            s_logger.LogInformation("Save request: {RequestType} {Id}", request.GetType(), request.Id);

            if (!_bus.HasOutbox())
                throw new InvalidOperationException("No outbox defined.");

            var messageMapper = _mapperRegistry.Get<T>();
            if (messageMapper == null)
                throw new ArgumentOutOfRangeException($"No message mapper registered for messages of type: {typeof(T)}");

            var message = messageMapper.MapToMessage(request);

            _bus.AddToOutbox(request, message, connectionProvider);

            return message.Id;
        }

        /// <summary>
        /// Adds a message into the outbox, and returns the id of the saved message.
        /// Intended for use with the Outbox pattern: http://gistlabs.com/2014/05/the-outbox/ normally you include the
        /// call to DepositPostBox within the scope of the transaction to write corresponding entity state to your
        /// database, that you want to signal via the request to downstream consumers
        /// Pass deposited Guid to <see cref="ClearOutboxAsync"/> 
        /// </summary>
        /// <param name="request">The request to save to the outbox</param>
        /// <param name="continueOnCapturedContext">Should we use the calling thread's synchronization context when continuing or a default thread synchronization context. Defaults to false</param>
        /// <param name="cancellationToken">The Cancellation Token.</param>
        /// <typeparam name="T">The type of the request</typeparam>
        /// <returns></returns>
        public async Task<Guid> DepositPostAsync<T>(T request, bool continueOnCapturedContext = false,
            CancellationToken cancellationToken = default(CancellationToken)) where T : class, IRequest
        {
            return await DepositPostAsync(request, _boxTransactionConnectionProvider, continueOnCapturedContext, cancellationToken);
        }
        
        private async Task<Guid> DepositPostAsync<T>(T request, IAmABoxTransactionConnectionProvider connectionProvider,  bool continueOnCapturedContext = false,
            CancellationToken cancellationToken = default(CancellationToken)) where T : class, IRequest
        {
            s_logger.LogInformation("Save request: {RequestType} {Id}", request.GetType(), request.Id);

            if (!_bus.HasAsyncOutbox())
                throw new InvalidOperationException("No async outbox defined.");

            var messageMapper = _mapperRegistry.Get<T>();
            if (messageMapper == null)
                throw new ArgumentOutOfRangeException($"No message mapper registered for messages of type: {typeof(T)}");

            var message = messageMapper.MapToMessage(request);

            await _bus.AddToOutboxAsync(request, continueOnCapturedContext, cancellationToken, message, _boxTransactionConnectionProvider);

            return message.Id;
        }


        /// <summary>
        /// Flushes the message box message given by <param name="posts"> to the broker.
        /// Intended for use with the Outbox pattern: http://gistlabs.com/2014/05/the-outbox/ <see cref="DepositPostBox"/>
        /// </summary>
        /// <param name="posts">The posts to flush</param>
        public void ClearOutbox(params Guid[] posts)
        {
            _bus.ClearOutbox(posts); 
        }

        /// <summary>
        /// Flushes the message box message given by <param name="posts"> to the broker.
        /// Intended for use with the Outbox pattern: http://gistlabs.com/2014/05/the-outbox/ <see cref="DepositPostBoxAsync"/>
        /// </summary>
        /// <param name="posts">The posts to flush</param>
        public async Task ClearOutboxAsync(
            IEnumerable<Guid> posts, 
            bool continueOnCapturedContext = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await _bus.ClearOutboxAsync(posts, continueOnCapturedContext, cancellationToken); 
        }


        /// <summary>
        /// Uses the Request-Reply messaging approach to send a message to another server and block awaiting a reply.
        /// The message is placed into a message queue but not into the outbox.
        /// An ephemeral reply queue is created, and its name used to set the reply address for the response. We produce
        /// a queue per exchange, to simplify correlating send and receive.
        /// The response is directed to a registered handler.
        /// Because the operation blocks, there is a mandatory timeout
        /// </summary>
        /// <param name="request">What message do we want a reply to</param>
        /// <param name="timeOutInMilliseconds">The call blocks, so we must time out</param>
        /// <exception cref="NotImplementedException"></exception>
        public TResponse Call<T, TResponse>(T request, int timeOutInMilliseconds)
            where T : class, ICall where TResponse : class, IResponse
        {
            if (timeOutInMilliseconds <= 0)
            {
                throw new InvalidOperationException("Timeout to a call method must have a duration greater than zero");
            }

            var outMessageMapper = _mapperRegistry.Get<T>();
            if (outMessageMapper == null)
                throw new ArgumentOutOfRangeException(
                    $"No message mapper registered for messages of type: {typeof(T)}");

            var inMessageMapper = _mapperRegistry.Get<TResponse>();
            if (inMessageMapper == null)
                throw new ArgumentOutOfRangeException(
                    $"No message mapper registered for messages of type: {typeof(T)}");
            
            var subscription = _replySubscriptions.FirstOrDefault(s => s.DataType == typeof(TResponse));

            if (subscription is null)
                throw new ArgumentOutOfRangeException($"No Subscription registered fpr replies of type {typeof(T)}");

            //create a reply queue via creating a consumer - we use random identifiers as we will destroy
            var channelName = Guid.NewGuid();
            var routingKey = channelName.ToString();

            subscription.ChannelName = new ChannelName(channelName.ToString());
            subscription.RoutingKey = new RoutingKey(routingKey);

            using (var responseChannel = _responseChannelFactory.CreateChannel(subscription))
            {
                s_logger.LogInformation("Create reply queue for topic {ChannelName}", channelName);
                request.ReplyAddress.Topic = routingKey;
                request.ReplyAddress.CorrelationId = channelName;

                //we do this to create the channel on the broker, or we won't have anything to send to; we 
                //retry in case the subscription is poor. An alternative would be to extract the code from
                //the channel to create the subscription, but this does not do much on a new queue
                _bus.Retry(() => responseChannel.Purge());

                var outMessage = outMessageMapper.MapToMessage(request);

                //We don't store the message, if we continue to fail further retry is left to the sender 
                //s_logger.LogDebug("Sending request  with routingkey {0}", routingKey);
                s_logger.LogDebug("Sending request  with routingkey {ChannelName}", channelName);
                _bus.SendViaExternalBus<T, TResponse>(outMessage);

                Message responseMessage = null;

                //now we block on the receiver to try and get the message, until timeout.
                s_logger.LogDebug("Awaiting response on {ChannelName}", channelName);
                _bus.Retry(() => responseMessage = responseChannel.Receive(timeOutInMilliseconds));

                TResponse response = default(TResponse);
                if (responseMessage.Header.MessageType != MessageType.MT_NONE)
                {
                    s_logger.LogDebug("Reply received from {ChannelName}", channelName);
                    //map to request is map to a response, but it is a request from consumer point of view. Confusing, but...
                    response = inMessageMapper.MapToRequest(responseMessage);
                    Send(response);
                }

                s_logger.LogInformation("Deleting queue for routingkey: {ChannelName}", channelName);

                return response;

            } //clean up everything at this point, whatever happens

        }

        /// <summary>
        /// The external service bus is a singleton as it has app lifetime to manage an Outbox.
        /// This method clears the external service bus, so that the next attempt to use it will create a fresh one
        /// It is mainly intended for testing, to allow the external service bus to be reset between tests
        /// </summary>
        public static void ClearExtServiceBus()
        {
            if (_bus != null)
            {
                lock (padlock)
                {
                    if (_bus != null)
                    {
                        _bus.Dispose();
                        _bus = null;
                    }
                }
            }
        }

        private void AssertValidSendPipeline<T>(T command, int handlerCount) where T : class, IRequest
        {
            s_logger.LogInformation("Found {HandlerCount} pipelines for command: {Type} {Id}", handlerCount, typeof(T), command.Id);

            if (handlerCount > 1)
                throw new ArgumentException($"More than one handler was found for the typeof command {typeof(T)} - a command should only have one handler.");
            if (handlerCount == 0)
                throw new ArgumentException($"No command handler was found for the typeof command {typeof(T)} - a command should have exactly one handler.");
        }

        //Create an instance of the ExternalBusServices if one not already set for this app. Note that we do not support reinitialization here, so once you have
        //set a command processor for the app, you can't call init again to set them - although the properties are not read-only so overwriting is possible
        //if needed as a "get out of gaol" card.
        private void InitExtServiceBus(IPolicyRegistry<string> policyRegistry,
            IAmAnOutbox<Message> outbox,
            int outboxTimeout,
            IAmAMessageProducer messageProducer)
        {
            if (_bus == null)
            {
                lock (padlock)
                {
                    if (_bus == null)
                    {
                        _bus = new ExternalBusServices();
                        if(outbox is IAmAnOutboxSync<Message> syncOutbox)_bus.OutBox = syncOutbox;
                        if(outbox is IAmAnOutboxAsync<Message> asyncOutbox)_bus.AsyncOutbox = asyncOutbox;

                        _bus.OutboxTimeout = outboxTimeout;
                        _bus.PolicyRegistry = policyRegistry;
                        if (messageProducer is IAmAMessageProducerSync syncMessageProducer)
                            _bus.MessageProducerSync = syncMessageProducer;
                        if (messageProducer is IAmAMessageProducerAsync asyncMessageProducer)
                            _bus.AsyncMessageProducer = asyncMessageProducer;
                    }
                }
            }
        }

    }
}
