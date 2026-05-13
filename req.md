No Oragon.RabbitMQ (pode consultar no context7) temos uma implementação fluente para o consumo de filas.

Nos meus projetos tenho a necessidade de publicar muitas mensagens, 

- na casa das centenas de milhares simultaneamente
- na casa de algumas por hora

essa variação é muito grande e exige 
- hora multiplas conexôes
- hora nenhuma ou uma única

A ideia do poll é ter um pool que seja generic

```
var pool = ElasticObjectPoolFactory.Build<IConnection>(sp, ct)

.Factory((sp, ct) => sp.GetRequiredService<IConnectionFactory>().CreateConnectionAsync(ct))

.Check((connection, ct) => connection.IsOpen
    ? Task.FromResult(PoolState.Healthy)
    : Task.FromResult(PoolState.Unhealthy))

.BeforeUse((connection, ct) => connection.IsOpen
    ? Task.FromResult(PoolState.Healthy)
    : Task.FromResult(PoolState.Unhealthy))

.AfterUse((connection, ct) => connection.IsOpen
    ? Task.FromResult(PoolState.Healthy)
    : Task.FromResult(PoolState.Unhealthy))

.Release((connection, ct) => connection.CloseAsync())

.Build();

using (var poolItem = pool.Accquire())
{
    IConnextion connection = poolItem.Object;

    connection.......
}

```