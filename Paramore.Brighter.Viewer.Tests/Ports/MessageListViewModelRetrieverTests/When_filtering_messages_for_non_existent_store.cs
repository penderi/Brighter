﻿using NUnit.Framework;
using Paramore.Brighter.MessageViewer.Adaptors.API.Resources;
using Paramore.Brighter.MessageViewer.Ports.Domain;
using Paramore.Brighter.MessageViewer.Ports.ViewModelRetrievers;
using Paramore.Brighter.Viewer.Tests.TestDoubles;

namespace Paramore.Brighter.Viewer.Tests.Ports.MessageListViewModelRetrieverTests
{
   public class MessageListVIewModelRetrieverFilterNonExistantStoreTests
    {
        private MessageListViewModelRetriever _messageListViewModelRetriever;
        private ViewModelRetrieverResult<MessageListModel, MessageListModelError> _result;
        private static string storeName = "storeNamenotInStore";

        [SetUp]
        public void Establish()
        {
            var modelFactory = FakeMessageStoreViewerFactory.CreateEmptyFactory();
            _messageListViewModelRetriever = new MessageListViewModelRetriever(modelFactory);
        }

        [Test]
        public void When_filtering_messages_for_non_existent_store()
        {
            _result = _messageListViewModelRetriever.Filter(storeName, "term");

            //should_not_return_MessageListModel
             var model = _result.Result;
            Assert.Null(model);
            Assert.True(_result.IsError);
            Assert.AreEqual(MessageListModelError.StoreNotFound, _result.Error);
        }
   }
}