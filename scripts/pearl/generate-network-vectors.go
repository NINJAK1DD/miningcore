// Run from the pinned Pearl checkout: go run /path/to/generate-network-vectors.go.
package main

import (
    "encoding/json"
    "os"
    "sort"
    "github.com/pearl-research-labs/pearl/node/chaincfg"
)
func main() {
    result := []map[string]interface{}{}
    for _, p := range []*chaincfg.Params{&chaincfg.MainNetParams, &chaincfg.TestNetParams, &chaincfg.TestNet2Params, &chaincfg.RegressionNetParams} {
        heights := map[int32]bool{0:true, 1:true}
        for _, fork := range []int32{p.MoEForkHeight,p.DenseOnlyForkHeight,p.RankPenaltyForkHeight,p.SaltedSeedForkHeight} {
            if fork > 0 { heights[fork-1]=true; heights[fork]=true; heights[fork+1]=true }
        }
        keys:=[]int{}
        for h := range heights { keys=append(keys,int(h)) }
        sort.Ints(keys)
        for _, value := range keys {
            h:=int32(value)
            result=append(result,map[string]interface{}{"network":p.Name,"height":h,"certificateVersion":uint32(p.RequiredCertVersion(h)),"denseOnly":p.IsDenseOnlyForkActive(h),"rankPenalty":p.IsRankPenaltyForkActive(h),"addressPrefix":p.Bech32HRPSegwit,"genesis":p.GenesisHash.String()})
        }
    }
    encoder:=json.NewEncoder(os.Stdout); encoder.SetIndent("","  ")
    if err:=encoder.Encode(result); err!=nil { panic(err) }
}
