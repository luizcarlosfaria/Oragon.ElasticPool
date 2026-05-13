# Heavy Resource Elasticity Benchmark

- Profile: `readme`
- Resource cost: `10 MB` per instance, `50 ms` creation delay, `15 ms` request hold time
- Max in-flight requests: `256`

Primary metric: `logical retained MB = live instances x resource MB`. Managed heap and working set are process-level hints and may lag behind object disposal.

| Strategy | Phase | Requested req/s | Achieved req/s | p95 ms | Created in phase | Disposed in phase | Live | Pool retained MB | Avg retained MB | MB*s retained | Peak pool total | Skipped |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| NoPool | 0 | 1 | 1 | 73.64 | 3 | 3 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoPool | 1 | 10 | 10 | 75.69 | 30 | 30 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoPool | 2 | 100 | 100 | 72.90 | 300 | 300 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoPool | 3 | 1,000 | 1,000 | 72.85 | 3,000 | 3,000 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoPool | 4 | 10,000 | 2,560 | 229.56 | 7,681 | 7,681 | 0 | 0 | 0 | 0 | 0 | 22,319 |
| NoPool | 5 | 20,000 | 3,285 | 92.29 | 9,854 | 9,854 | 0 | 0 | 0 | 0 | 0 | 50,146 |
| NoPool | 6 | 30,000 | 3,343 | 87.64 | 10,028 | 10,028 | 0 | 0 | 0 | 0 | 0 | 79,972 |
| NoPool | 7 | 20,000 | 3,415 | 83.61 | 10,246 | 10,246 | 0 | 0 | 0 | 0 | 0 | 49,754 |
| NoPool | 8 | 10,000 | 3,413 | 84.83 | 10,240 | 10,240 | 0 | 0 | 0 | 0 | 0 | 19,760 |
| NoPool | 9 | 1,000 | 1,000 | 72.57 | 3,000 | 3,000 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoPool | 10 | 100 | 100 | 72.55 | 300 | 300 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoPool | 11 | 10 | 10 | 76.00 | 30 | 30 | 0 | 0 | 0 | 0 | 0 | 0 |
| NoPool | 12 | 1 | 1 | 78.98 | 3 | 3 | 0 | 0 | 0 | 0 | 0 | 0 |
| ElasticPool | 0 | 1 | 1 | 71.55 | 1 | 0 | 1 | 10 | 7 | 20 | 1 | 0 |
| ElasticPool | 1 | 10 | 10 | 20.50 | 1 | 1 | 1 | 10 | 10 | 30 | 1 | 0 |
| ElasticPool | 2 | 100 | 100 | 20.35 | 4 | 2 | 3 | 30 | 42 | 128 | 5 | 0 |
| ElasticPool | 3 | 1,000 | 1,000 | 20.43 | 77 | 0 | 80 | 800 | 786 | 2,373 | 80 | 0 |
| ElasticPool | 4 | 10,000 | 9,842 | 20.36 | 176 | 0 | 256 | 2,560 | 2,545 | 7,681 | 256 | 474 |
| ElasticPool | 5 | 20,000 | 12,593 | 19.77 | 0 | 0 | 256 | 2,560 | 2,560 | 7,732 | 256 | 22,222 |
| ElasticPool | 6 | 30,000 | 12,804 | 20.37 | 0 | 0 | 256 | 2,560 | 2,560 | 7,734 | 256 | 51,589 |
| ElasticPool | 7 | 20,000 | 12,568 | 20.33 | 0 | 0 | 256 | 2,560 | 2,560 | 7,729 | 256 | 22,295 |
| ElasticPool | 8 | 10,000 | 9,969 | 20.26 | 0 | 0 | 256 | 2,560 | 2,560 | 7,738 | 256 | 94 |
| ElasticPool | 9 | 1,000 | 1,000 | 20.32 | 0 | 0 | 256 | 2,560 | 2,560 | 7,728 | 256 | 0 |
| ElasticPool | 10 | 100 | 100 | 19.44 | 0 | 96 | 160 | 1,600 | 2,169 | 6,553 | 256 | 0 |
| ElasticPool | 11 | 10 | 10 | 20.34 | 0 | 96 | 64 | 640 | 1,203 | 3,633 | 160 | 0 |
| ElasticPool | 12 | 1 | 1 | 67.95 | 1 | 64 | 1 | 10 | 268 | 811 | 64 | 0 |
| Microsoft.Extensions.ObjectPool | 0 | 1 | 1 | 67.92 | 1 | 0 | 1 | 10 | 7 | 20 | 1 | 0 |
| Microsoft.Extensions.ObjectPool | 1 | 10 | 10 | 20.17 | 0 | 0 | 1 | 10 | 10 | 30 | 1 | 0 |
| Microsoft.Extensions.ObjectPool | 2 | 100 | 100 | 20.31 | 5 | 0 | 6 | 60 | 58 | 176 | 6 | 0 |
| Microsoft.Extensions.ObjectPool | 3 | 1,000 | 1,000 | 20.35 | 56 | 0 | 62 | 620 | 600 | 1,811 | 62 | 0 |
| Microsoft.Extensions.ObjectPool | 4 | 10,000 | 9,428 | 20.39 | 182 | 0 | 244 | 2,440 | 2,291 | 6,919 | 244 | 1,715 |
| Microsoft.Extensions.ObjectPool | 5 | 20,000 | 12,690 | 20.29 | 12 | 0 | 256 | 2,560 | 2,557 | 7,733 | 256 | 21,929 |
| Microsoft.Extensions.ObjectPool | 6 | 30,000 | 12,808 | 20.36 | 0 | 0 | 256 | 2,560 | 2,560 | 7,717 | 256 | 51,575 |
| Microsoft.Extensions.ObjectPool | 7 | 20,000 | 12,656 | 20.28 | 0 | 0 | 256 | 2,560 | 2,560 | 7,726 | 256 | 22,033 |
| Microsoft.Extensions.ObjectPool | 8 | 10,000 | 9,966 | 20.29 | 0 | 0 | 256 | 2,560 | 2,560 | 7,725 | 256 | 103 |
| Microsoft.Extensions.ObjectPool | 9 | 1,000 | 1,000 | 20.25 | 0 | 0 | 256 | 2,560 | 2,560 | 7,741 | 256 | 0 |
| Microsoft.Extensions.ObjectPool | 10 | 100 | 100 | 20.30 | 0 | 0 | 256 | 2,560 | 2,560 | 7,743 | 256 | 0 |
| Microsoft.Extensions.ObjectPool | 11 | 10 | 10 | 20.24 | 0 | 0 | 256 | 2,560 | 2,560 | 7,727 | 256 | 0 |
| Microsoft.Extensions.ObjectPool | 12 | 1 | 1 | 15.92 | 0 | 0 | 256 | 2,560 | 2,560 | 7,731 | 256 | 0 |

## Measured elasticity

| Strategy | Created during run | Disposed during run | Peak pool total | Retained MB*s | Average retained MB | Final retained MB |
|---|---:|---:|---:|---:|---:|---:|
| NoPool | 54,715 | 54,715 | 0 | 0 | 0 | 0 |
| ElasticPool | 260 | 260 | 256 | 59,888 | 1,525 | 0 |
| Microsoft.Extensions.ObjectPool | 256 | 0 | 256 | 70,797 | 1,804 | 2,560 |

## ElasticPool vs ObjectPool logical retention

| Phase | Requested req/s | ElasticPool retained MB | ObjectPool retained MB | ObjectPool excess MB | Retention factor |
|---:|---:|---:|---:|---:|---:|
| 0 | 1 | 10 | 10 | 0 | 1.0x |
| 1 | 10 | 10 | 10 | 0 | 1.0x |
| 2 | 100 | 30 | 60 | 30 | 2.0x |
| 3 | 1,000 | 800 | 620 | -180 | 0.8x |
| 4 | 10,000 | 2,560 | 2,440 | -120 | 1.0x |
| 5 | 20,000 | 2,560 | 2,560 | 0 | 1.0x |
| 6 | 30,000 | 2,560 | 2,560 | 0 | 1.0x |
| 7 | 20,000 | 2,560 | 2,560 | 0 | 1.0x |
| 8 | 10,000 | 2,560 | 2,560 | 0 | 1.0x |
| 9 | 1,000 | 2,560 | 2,560 | 0 | 1.0x |
| 10 | 100 | 1,600 | 2,560 | 960 | 1.6x |
| 11 | 10 | 640 | 2,560 | 1,920 | 4.0x |
| 12 | 1 | 10 | 2,560 | 2,550 | 256.0x |

## Post-cooldown summary

| Strategy | Created | Disposed | Live | Logical retained MB | Logical disposed MB | Final managed MB | Final working set MB | Final pool total |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| NoPool | 54,715 | 54,715 | 0 | 0 | 547,150 | 11.5 | 7,619.0 | 0 |
| ElasticPool | 260 | 260 | 0 | 0 | 2,600 | 2,625.9 | 2,696.7 | 0 |
| Microsoft.Extensions.ObjectPool | 256 | 0 | 256 | 2,560 | 0 | 2,601.9 | 2,693.6 | 256 |
