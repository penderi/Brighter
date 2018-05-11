﻿using System;

namespace Paramore.Brighter.Tests.CommandProcessors.TestDoubles
{
    public class MyRequest : Request
    {
        public string RequestValue { get; set; }
        
        public MyRequest() : this(new ReplyAddress()) {}

        public MyRequest(ReplyAddress replyAddress) : base(replyAddress)
        {
        }
        

   }
}
