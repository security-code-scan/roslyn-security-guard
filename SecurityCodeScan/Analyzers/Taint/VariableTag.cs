﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SecurityCodeScan.Analyzers.Taint
{
    public class VariableTag
    {
        public VariableTag(Tag tag, object value = null)
        {
            Tag   = tag;
            Value = value;
        }

        public Tag    Tag;
        public object Value;

    }
}
