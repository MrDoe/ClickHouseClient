﻿#region License Apache 2.0
/* Copyright 2021, 2024 Octonica
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

#if NET6_0_OR_GREATER

using System;

namespace Octonica.ClickHouseClient.Types
{
    partial class Date32TableColumn : IClickHouseTableColumn<DateOnly>
    {
        private static readonly DateOnly UnixEpoch = DateOnly.FromDateTime(DateTime.UnixEpoch);

        DateOnly IClickHouseTableColumn<DateOnly>.DefaultValue => default;

        public DateOnly GetValue(int index)
        {
            var value = _buffer.Span[index];
            if (value == DefaultValue)
                return default;

            return UnixEpoch.AddDays(value);
        }

        public IClickHouseTableColumn<T>? TryReinterpret<T>()
        {
            if (typeof(T) == typeof(DateTime))
                return (IClickHouseTableColumn<T>)(object)new ReinterpretedTableColumn<DateOnly, DateTime>(this, dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue));

            if (typeof(T) == typeof(DateTime?))
                return (IClickHouseTableColumn<T>)(object)new NullableStructTableColumn<DateTime>(null, new ReinterpretedTableColumn<DateOnly, DateTime>(this, dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue)));

            if (typeof(T) == typeof(DateOnly?))
                return (IClickHouseTableColumn<T>)(object)new NullableStructTableColumn<DateOnly>(null, this);

            return null;
        }
    }
}

#endif