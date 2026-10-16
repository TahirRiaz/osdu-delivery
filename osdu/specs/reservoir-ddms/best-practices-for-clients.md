# Best Practices for ETP clients

This document regroups some information for developers implementing clients. Its goal is to ease interactions with the server to have more robust and efficient data flow.

## Message Size

As part of the ETP specifications, the message size is negotiated between the client and server. While this server may be able to handle large message sizes (configurable during deployment), it is not recommended to use messages in the GB range, as this will degrade performance.  
Once the message size is negotiated, you can optimize ingestion speed by filling the message content without exceeding its allocated size. For messages supporting multiple objects or arrays, try to group them to maximize the message size utilization while ensuring you do not exceed the limit.

## Creation of Dataspace

Starting with M25, creating a dataspace in OSDU requires providing ACLs and legal tags at the time of creation. This is done by including the following metadata:  
`["viewers", "owners", "legaltags", "otherRelevantDataCountries"]`

## Exchange of Large Arrays

Some large arrays may exceed the negotiated message size. 
* GET: Before getting or putting an array, ensure its size does not exceed the limit. If it does, use subarray messages (`GetDataSubarrays` or `PutDataSubarrays`) as needed.  
Before retrieving an array, it is recommended to first obtain its metadata using the `GetDataArrayMetadata` message.
* PUT: Some large arrays may require significant time to transfer the data. In some network configuration, the connection may be interrupted during transactions. In such configuration, it is recommended to limit the duration of transactions and be ready to retry the failed ones. In order to shorten the transaction time, one strategy is to split potentially long transactions into mutiple transactions where:
  - The first transaction contains the objects (XML,JSON) and the messages initializing the large arrays (`PutUninitializedDataArrays`). At this point the large arrays are only initialized (empty) but not do not contain actual values.
  - Additional transactions will fill the large arrays with their actual values, using `PutDataSubarrays` if needed. Those transactions can be individually retried when connections are lost.
This ensures that the referential integrity of each transactions is respected, but the duration of individual transaction remains limited.

## Transactions

Using the transaction protocol is highly recommended for several reasons.  
A transaction acts as a "snapshot point" for the database before data exchange.

### During Read Access

Transactions ensure that all queries within the same transaction are consistent. This prevents inconsistent relationships when accessing multiple objects, even if other clients are writing to the database between requests.

### During Write Access

It is crucial to use transactions when writing data and to specify the dataspace in which you are working. Transactions provide the following benefits:

* **Consistency**: All XML and binary data (arrays) remain consistent, even in cases of disconnection or other interruptions. Since multiple messages from various protocols may be required to transition a dataspace to a consistent state, committing these changes within a transaction ensures consistency. Transactions also simplify rollbacks in case of issues or disconnections. During a transaction, only the current session can query the content added as part of the transaction, preventing interference with other clients reading the same dataspace.

* **Order of Operations**: The server enforces complete relationships between objects and their binary data. This consistency is verified with each request unless the requests are part of a transaction. When transactions are used, validation occurs at the end of the transaction. This allows clients to send data in any order without requiring the referenced data to be present before ingestion.

* **Database Deadlock vs. Performance**: To avoid potential deadlocks with a high number of concurrent writes, only a single ETP transaction is allowed to write to a given dataspace at a time. However, separate dataspaces can be written to concurrently, and calls within a transaction can also be concurrent. When transactions are not used, the server creates local transactions internally, which may significantly slow down the ingestion process.

**Retries**: Since only one transaction at a time can write to a dataspace, creating a new transaction will fail (with a `MAX_TRANSACTIONS_EXCEEDED` error) if another client is already engaged in a write transaction for the same dataspace.  
To handle this, it is recommended to use only one dataspace per write transaction and implement a retry mechanism to handle transaction creation failures.

## Lock

The RDMS supports both a system of engagement and a system of record. The key difference between these two modes is the read-only status of a dataspace.  
Between the creation of a dataspace and the validation of its content, the dataspace must remain in a read-write state. However, when the content needs to be published to the system of record, the client should lock the dataspace using the `LockDataspaces` message. This action sets the dataspace to read-only, ensuring compliance with OSDU principles.