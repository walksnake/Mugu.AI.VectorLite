# 过滤器与混合查询

> 本章详解 VectorLite 的 7 种过滤表达式及其组合策略。
>
> 命名空间：`Mugu.AI.VectorLite.Engine`

---

## 过滤表达式类型总览

所有过滤表达式继承自抽象基类 `FilterExpression`，提供 `Evaluate(Dictionary<string, object>? metadata)` 方法。

| 类型 | 说明 | 构造方式 |
|------|------|----------|
| `EqualFilter` | 精确匹配 | `new EqualFilter("field", value)` |
| `NotEqualFilter` | 不等于 | `new NotEqualFilter("field", value)` |
| `InFilter` | 值在集合中 | `new InFilter("field", values)` |
| `RangeFilter` | 范围过滤（支持开区间/闭区间） | `new RangeFilter("field", lower, upper)` |
| `AndFilter` | 逻辑与 | `new AndFilter(expr1, expr2, ...)` |
| `OrFilter` | 逻辑或 | `new OrFilter(expr1, expr2, ...)` |
| `NotFilter` | 逻辑非 | `new NotFilter(expr)` |

---

## 详细签名与语义

### EqualFilter

```csharp
public sealed class EqualFilter : FilterExpression
{
    public string Field { get; }     // 元数据键名
    public object Value { get; }     // 期望值

    public EqualFilter(string field, object value);
}
```

匹配规则：`metadata[field]?.Equals(value) == true`。字段不存在时不匹配。

### NotEqualFilter

```csharp
public sealed class NotEqualFilter : FilterExpression
{
    public string Field { get; }
    public object Value { get; }

    public NotEqualFilter(string field, object value);
}
```

匹配规则：`metadata[field]` 存在且不等于 `value`。字段不存在时**不匹配**。

### InFilter

```csharp
public sealed class InFilter : FilterExpression
{
    public string Field { get; }
    public IReadOnlyList<object> Values { get; }

    public InFilter(string field, IEnumerable<object> values);
}
```

匹配规则：`Values.Any(v => metadata[field]?.Equals(v) == true)`。

### RangeFilter

```csharp
public sealed class RangeFilter : FilterExpression
{
    public string Field { get; }
    public IComparable? LowerBound { get; }    // null = 不设下界
    public IComparable? UpperBound { get; }    // null = 不设上界
    public bool LowerInclusive { get; }        // 默认 true
    public bool UpperInclusive { get; }        // 默认 true

    public RangeFilter(
        string field,
        IComparable? lowerBound = null,
        IComparable? upperBound = null,
        bool lowerInclusive = true,
        bool upperInclusive = true);
}
```

匹配规则：值转为 `IComparable` 后与上下界比较。

示例：

```csharp
// priority >= 5 AND priority <= 10（闭区间）
new RangeFilter("priority", lowerBound: 5L, upperBound: 10L)

// priority > 5（开左界）
new RangeFilter("priority", lowerBound: 5L, lowerInclusive: false)

// priority <= 10（仅上界）
new RangeFilter("priority", upperBound: 10L)
```

### AndFilter

```csharp
public sealed class AndFilter : FilterExpression
{
    public IReadOnlyList<FilterExpression> Expressions { get; }

    public AndFilter(params FilterExpression[] expressions);
    public AndFilter(IEnumerable<FilterExpression> expressions);
}
```

短路求值：任一子表达式为 `false` 即返回 `false`。

### OrFilter

```csharp
public sealed class OrFilter : FilterExpression
{
    public IReadOnlyList<FilterExpression> Expressions { get; }

    public OrFilter(params FilterExpression[] expressions);
    public OrFilter(IEnumerable<FilterExpression> expressions);
}
```

短路求值：任一子表达式为 `true` 即返回 `true`。

### NotFilter

```csharp
public sealed class NotFilter : FilterExpression
{
    public FilterExpression Expression { get; }

    public NotFilter(FilterExpression expression);
}
```

---

## 查询构建器的过滤行为

### 简写与全写

```csharp
// 简写：自动转换为 EqualFilter
.Where("tag", "工作")

// 等价全写
.Where(new EqualFilter("tag", "工作"))
```

### 多个 Where 的合并规则

多次调用 `.Where()` 时，查询构建器自动将所有过滤器用 `AndFilter` 合并：

```csharp
// 这两种写法完全等价
collection.Query(vec)
    .Where("category", "技术")
    .Where(new RangeFilter("score", lowerBound: 80L))
    .ToListAsync();

collection.Query(vec)
    .Where(new AndFilter(
        new EqualFilter("category", "技术"),
        new RangeFilter("score", lowerBound: 80L)
    ))
    .ToListAsync();
```

### OR 查询

OR 必须显式构造：

```csharp
collection.Query(vec)
    .Where(new OrFilter(
        new EqualFilter("tag", "工作"),
        new EqualFilter("tag", "学习")
    ))
    .ToListAsync();
```

---

## 混合查询执行流程

```
Query(vector)                     ← 用户提交查询
    │
    ▼
ScalarIndex.Search(filter)        ← 第一步：利用倒排索引快速过滤
    │                               输出候选 ID 集合
    ▼
HNSWIndex.Search(vector,          ← 第二步：在候选集合内做向量近邻搜索
    k, efSearch, candidateIds)       输出 Top-K 结果
    │
    ▼
结果排序 + minScore 过滤          ← 第三步：按距离升序，丢弃不满足阈值的结果
    │
    ▼
SearchResult[]                    ← 返回给调用者
```

### 性能建议

| 策略 | 说明 |
|------|------|
| **选择性高的字段优先过滤** | 枚举字段（如 `category`）比范围字段更高效 |
| **避免全量 OR 扫描** | `OrFilter` 对每个子过滤器分别执行倒排查找并取并集 |
| **使用 `InFilter` 替代多个 `EqualFilter` 的 OR** | `InFilter("tag", ["A","B","C"])` 比 `OrFilter(Equal, Equal, Equal)` 更高效 |
| **增大 `efSearch` 提高召回** | 过滤后候选集较小时，增大 ef 可避免遗漏 |
| **`WithMinScore` 后置过滤** | minScore 在 HNSW 搜索之后执行，不影响搜索性能 |

---

## 标量索引如何工作

标量索引（`ScalarIndex`）为每个元数据字段维护一个**倒排索引**：

```
field → value → HashSet<ulong>（记录 ID 集合）
```

- **写入时**：自动索引记录的所有元数据键值对
- **查询时**：
  - `EqualFilter`: 直接查找 `index[field][value]`，O(1)
  - `InFilter`: 查找多个值的集合取并集
  - `RangeFilter`: 遍历字段的所有值，逐一比较（适合小基数字段）
  - `NotEqualFilter`: 遍历字段的所有值，排除匹配的
  - `AndFilter`: 各子过滤器结果取交集
  - `OrFilter`: 各子过滤器结果取并集
  - `NotFilter`: 全集 - 子过滤器结果

> ⚠️ `RangeFilter` 在高基数字段（如时间戳）上效率较低。后续版本可能引入有序索引优化。
