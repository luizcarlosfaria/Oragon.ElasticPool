# Heavy Resource Elasticity Benchmark

- Profile: `readme`
- Resource cost: `10 MB` per instance, `50 ms` creation delay, `5 ms` request hold time
- Max in-flight requests: `256`

Primary metric: `logical retained MB = live instances x resource MB`. Managed heap and working set are process-level hints and may lag behind object disposal.

| Strategy | Phase | Requested req/s | Achieved req/s | p95 ms | Created | Live | Logical retained MB | Pool retained MB | Managed MB | Working set MB | Skipped |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| NoPool | 0 | 1 | 1 | 60.59 | 3 | 0 | 0 | 0 | 20.1 | 54.0 | 0 |
| NoPool | 1 | 10 | 10 | 63.89 | 33 | 0 | 0 | 0 | 20.1 | 66.6 | 0 |
| NoPool | 2 | 100 | 100 | 64.12 | 333 | 0 | 0 | 0 | 20.2 | 57.7 | 0 |
| NoPool | 3 | 1,000 | 1,000 | 64.22 | 3,333 | 0 | 0 | 0 | 700.6 | 769.1 | 0 |
| NoPool | 4 | 10,000 | 1,225 | 444.84 | 7,008 | 0 | 0 | 0 | 1,800.9 | 15,840.7 | 26,325 |
| NoPool | 5 | 1,000 | 1,000 | 63.26 | 10,008 | 0 | 0 | 0 | 80.7 | 14,984.9 | 0 |
| NoPool | 6 | 100 | 100 | 62.56 | 10,308 | 0 | 0 | 0 | 41.8 | 14,221.2 | 0 |
| NoPool | 7 | 10 | 10 | 64.30 | 10,338 | 0 | 0 | 0 | 11.8 | 13,310.6 | 0 |
| NoPool | 8 | 1 | 1 | 72.00 | 10,341 | 0 | 0 | 0 | 21.9 | 12,990.5 | 0 |
| AdaptivePool | 0 | 1 | 1 | 63.14 | 1 | 1 | 10 | 10 | 10.4 | 11,379.2 | 0 |
| AdaptivePool | 1 | 10 | 10 | 12.94 | 2 | 1 | 10 | 10 | 10.2 | 10,577.8 | 0 |
| AdaptivePool | 2 | 100 | 100 | 11.89 | 11 | 7 | 70 | 70 | 100.5 | 10,409.7 | 0 |
| AdaptivePool | 3 | 1,000 | 1,000 | 11.85 | 41 | 19 | 190 | 190 | 372.6 | 9,930.6 | 0 |
| AdaptivePool | 4 | 10,000 | 9,764 | 11.85 | 278 | 192 | 1,920 | 1,920 | 2,582.9 | 9,472.8 | 708 |
| AdaptivePool | 5 | 1,000 | 1,000 | 11.76 | 278 | 96 | 960 | 960 | 2,585.6 | 9,475.8 | 0 |
| AdaptivePool | 6 | 100 | 100 | 11.62 | 278 | 2 | 20 | 20 | 2,586.0 | 9,477.3 | 0 |
| AdaptivePool | 7 | 10 | 10 | 55.88 | 280 | 1 | 10 | 10 | 2,606.2 | 9,477.5 | 0 |
| AdaptivePool | 8 | 1 | 1 | 60.66 | 281 | 1 | 10 | 10 | 2,616.4 | 9,477.5 | 0 |
| Microsoft.Extensions.ObjectPool | 0 | 1 | 1 | 55.26 | 1 | 1 | 10 | 10 | 10.9 | 7,889.5 | 0 |
| Microsoft.Extensions.ObjectPool | 1 | 10 | 10 | 11.69 | 1 | 1 | 10 | 10 | 11.1 | 7,889.5 | 0 |
| Microsoft.Extensions.ObjectPool | 2 | 100 | 100 | 11.73 | 6 | 6 | 60 | 60 | 61.1 | 6,895.5 | 0 |
| Microsoft.Extensions.ObjectPool | 3 | 1,000 | 1,000 | 11.75 | 30 | 30 | 300 | 300 | 302.9 | 6,415.6 | 0 |
| Microsoft.Extensions.ObjectPool | 4 | 10,000 | 9,719 | 11.86 | 175 | 175 | 1,750 | 1,750 | 1,770.2 | 5,936.6 | 842 |
| Microsoft.Extensions.ObjectPool | 5 | 1,000 | 1,000 | 11.76 | 175 | 175 | 1,750 | 1,750 | 1,772.6 | 5,938.3 | 0 |
| Microsoft.Extensions.ObjectPool | 6 | 100 | 100 | 11.80 | 175 | 175 | 1,750 | 1,750 | 1,773.0 | 5,938.8 | 0 |
| Microsoft.Extensions.ObjectPool | 7 | 10 | 10 | 11.89 | 175 | 175 | 1,750 | 1,750 | 1,773.2 | 5,939.1 | 0 |
| Microsoft.Extensions.ObjectPool | 8 | 1 | 1 | 8.82 | 175 | 175 | 1,750 | 1,750 | 1,773.4 | 5,939.1 | 0 |

## AdaptivePool vs ObjectPool logical retention

| Phase | Requested req/s | AdaptivePool retained MB | ObjectPool retained MB | ObjectPool excess MB | Retention factor |
|---:|---:|---:|---:|---:|---:|
| 0 | 1 | 10 | 10 | 0 | 1.0x |
| 1 | 10 | 10 | 10 | 0 | 1.0x |
| 2 | 100 | 70 | 60 | -10 | 0.9x |
| 3 | 1,000 | 190 | 300 | 110 | 1.6x |
| 4 | 10,000 | 1,920 | 1,750 | -170 | 0.9x |
| 5 | 1,000 | 960 | 1,750 | 790 | 1.8x |
| 6 | 100 | 20 | 1,750 | 1,730 | 87.5x |
| 7 | 10 | 10 | 1,750 | 1,740 | 175.0x |
| 8 | 1 | 10 | 1,750 | 1,740 | 175.0x |

## Post-cooldown summary

| Strategy | Created | Disposed | Live | Logical retained MB | Logical disposed MB | Final managed MB | Final working set MB | Final pool total |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| NoPool | 10,341 | 10,341 | 0 | 0 | 103,410 | 22.0 | 12,990.5 | 0 |
| AdaptivePool | 281 | 281 | 0 | 0 | 2,810 | 2,616.5 | 9,476.0 | 0 |
| Microsoft.Extensions.ObjectPool | 175 | 0 | 175 | 1,750 | 0 | 1,773.5 | 5,939.3 | 175 |
