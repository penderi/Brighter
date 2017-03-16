﻿#region Licence

/* The MIT License (MIT)
Copyright © 2014 Francesco Pighi <francesco.pighi@gmail.com>

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
using NUnit.Framework;
using Paramore.Brighter.Messagestore.MsSql;

namespace Paramore.Brighter.Tests.MessageStore.MsSql
{
    [Category("MSSQL")]
    [TestFixture]
    public class MsSqlMessageStoreRangeRequestTests
    {
        private MsSqlTestHelper _msSqlTestHelper;
        private readonly string _TopicFirstMessage = "test_topic";
        private readonly string _TopicLastMessage = "test_topic3";
        private IEnumerable<Message> messages;
        private Message s_message1;
        private Message s_message2;
        private Message s_messageEarliest;
        private MsSqlMessageStore s_sqlMessageStore;

        [SetUp]
        public void Establish()
        {
            _msSqlTestHelper = new MsSqlTestHelper();
            _msSqlTestHelper.SetupMessageDb();

            s_sqlMessageStore = new MsSqlMessageStore(_msSqlTestHelper.MessageStoreConfiguration);
            s_messageEarliest = new Message(new MessageHeader(Guid.NewGuid(), _TopicFirstMessage, MessageType.MT_DOCUMENT), new MessageBody("message body"));
            s_message1 = new Message(new MessageHeader(Guid.NewGuid(), "test_topic2", MessageType.MT_DOCUMENT), new MessageBody("message body2"));
            s_message2 = new Message(new MessageHeader(Guid.NewGuid(), _TopicLastMessage, MessageType.MT_DOCUMENT), new MessageBody("message body3"));
            s_sqlMessageStore.Add(s_messageEarliest);
            s_sqlMessageStore.Add(s_message1);
            s_sqlMessageStore.Add(s_message2);
        }

        [Test]
        public void When_There_Are_Multiple_Messages_In_The_Message_Store_And_A_Range_Is_Fetched()
        {
            messages = s_sqlMessageStore.Get(1, 3);

            //_should_fetch_1_message
            Assert.AreEqual(1, messages.Count());
            //_should_fetch_expected_message
            Assert.AreEqual(_TopicLastMessage, messages.First().Header.Topic);
            //_should_not_fetch_null_messages
            Assert.NotNull(messages);
        }


        [TearDown]
        public void Cleanup()
        {
            CleanUpDb();
        }

        private void CleanUpDb()
        {
            _msSqlTestHelper.CleanUpDb();
        }
    }
}