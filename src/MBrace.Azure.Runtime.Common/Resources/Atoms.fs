﻿namespace Nessos.MBrace.Azure.Runtime.Resources

open System
open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table
open Nessos.MBrace.Azure.Runtime
open Nessos.MBrace.Azure.Runtime.Common
open Nessos.MBrace.Store
open Nessos.MBrace

[<AutoSerializableAttribute(true)>]
type Atom<'T> internal (table, pk, rk, config) =
    interface ICloudAtom<'T> with

        member this.Value : 'T = Async.RunSynchronously((this :> ICloudAtom<'T>).GetValue())
        
        member this.Id = sprintf "%s/%s/%s" table pk rk

        member this.Dispose(): Async<unit> = 
            async {
                let! e = Table.read<FatEntity> config table pk rk
                return! Table.delete<FatEntity> config table e
            }
        
        member this.Update(updater: 'T -> 'T, ?maxRetries : int): Async<unit> = 
            async {
                let pickler = Configuration.Serializer
                let interval = let r = new Random() in r.Next(2,10) 
                let maxInterval = 5000
                let maxRetries = defaultArg maxRetries Int32.MaxValue
                let rec update currInterval count = async {
                    if count >= maxRetries then
                        return raise <| exn("Maximum number of retries exceeded.")
                    else
                        let! e = Table.read<FatEntity> config table pk rk
                        let oldValue = pickler.UnPickle<'T>(e.GetPayload())
                        let newValue = updater oldValue
                        let newBinary = pickler.Pickle<'T>(newValue)
                        let e = new FatEntity(e.PartitionKey, String.Empty, newBinary, ETag = e.ETag)
                        let! result = Async.Catch <| Table.merge config table e
                        match result with
                        | Choice1Of2 _ -> return ()
                        | Choice2Of2 e when Table.PreconditionFailed e -> 
                            do! Async.Sleep currInterval
                            return! update (min (interval * currInterval) maxInterval) (count+1)
                        | Choice2Of2 e -> return raise e
                }

                return! update interval 0
            }       

        member this.Force(newValue: 'T): Async<unit> = 
            async {
                let! e = Table.read<FatEntity> config table pk rk
                let newBinary = Configuration.Serializer.Pickle<'T>(newValue)
                let e = new FatEntity(e.PartitionKey, String.Empty, newBinary, ETag = "*")
                let! _ = Table.merge config table e
                return ()
            }

        member this.GetValue(): Async<'T> = 
            async {
                let! e = Table.read<FatEntity> config table pk rk
                let value = Configuration.Serializer.UnPickle<'T>(e.GetPayload())
                return value
            }

[<AutoSerializableAttribute(false)>]
type AtomProvider private(config : ConfigurationId) =
        
    interface ICloudAtomProvider with
        member this.Id   = "CloudAtomProvider" 
        member this.Name = "CloudAtomProvider" 

        member this.IsSupportedValue(value: 'T) : bool = 
            Configuration.Serializer.ComputeSize(value) <= TableEntityUtils.MaxPayloadSize
        
        member this.CreateUniqueContainerName() = (guid()).Substring(0,5) // TODO : Change

        member this.CreateAtom(container, initial: 'T) = 
                async {
                    let binary = Configuration.Serializer.Pickle(initial)
                    let e = new FatEntity(guid(), String.Empty, binary)
                    do! Table.insert<FatEntity> config container e
                    return new Atom<'T>(container, e.PartitionKey, e.RowKey, config) :> ICloudAtom<'T>
                }

        member this.DisposeContainer(container) =
            async {
                do! ConfigurationRegistry.Resolve<ClientProvider>(config).TableClient.GetTableReference(container).DeleteIfExistsAsync()
            }

        member this.GetAtomProviderDescriptor() : ICloudAtomProviderDescriptor = 
            let this = this :> ICloudAtomProvider
            let id = this.Id
            let name = this.Name
            let config = config
            { new ICloudAtomProviderDescriptor with
                  member x.Id : string = id
                  member x.Name : string = name
                  member x.Recover() : ICloudAtomProvider = new AtomProvider(config) :> _
            }

    static member Create(config : ConfigurationId) : ICloudAtomProvider =
        new AtomProvider(config) :> _
