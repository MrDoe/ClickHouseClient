﻿#region License Apache 2.0
/* Copyright 2019-2021, 2024 Octonica
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
#endregion

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Octonica.ClickHouseClient.Types
{
    internal sealed class FixedStringTableColumn : FixedStringTableColumnBase<byte[]>
    {
        public override byte[] DefaultValue => Array.Empty<byte>();

        public FixedStringTableColumn(Memory<byte> buffer, int rowSize, Encoding encoding)
            : base(buffer, rowSize, encoding)
        {
        }

        [return: NotNull]
        protected override byte[] GetValue(Encoding encoding, ReadOnlySpan<byte> span)
        {
            var result = new byte[span.Length];
            span.CopyTo(result);
            return result;
        }
    }
}
