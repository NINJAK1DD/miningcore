// Run with "go run /path/to/generate-wire-vectors.go" from the pinned Pearl checkout.
// Fixtures exercise wire serialization only; their proof payload is deliberately not valid.
package main
import (
    "bytes"
    "encoding/hex"
    "encoding/json"
    "os"
    "time"
    "github.com/pearl-research-labs/pearl/node/wire"
)
func check(err error) { if err != nil { panic(err) } }
func main() {
    var fixtures []map[string]any
    for version := uint32(1); version <= 3; version++ {
        var public [164]byte
        for i := range public { public[i] = byte(i) }
        proof := []byte{0xde, 0xad, 0xbe, 0xef}
        h := wire.BlockHeader{Version: 1, Timestamp: time.Unix(0x495fab29, 0), Bits: 0x1d00ffff}
        prev, err := hex.DecodeString("6fe28c0ab6f1b372c1a6a246ae63f74f931e8365e15a089c68d6190000000000")
        check(err)
        merkle, err := hex.DecodeString("3ba3edfd7a7b12b27ac72c3e67768f617fc81bc3888a51323a9fb8aa4b1e5e4a")
        check(err)
        copy(h.PrevBlock[:], prev)
        copy(h.MerkleRoot[:], merkle)
        c1 := &wire.CertificateV1{PublicData: public, ProofData: proof}
        c2 := wire.CertificateV2{PublicDataLen: 164, ProofData: proof}
        copy(c2.PublicData[:], public[:])
        c3 := &wire.CertificateV3{CertificateV2: c2}
        var cert wire.BlockCertificate
        switch version { case 1: cert = c1; case 2: cert = &c2; case 3: cert = c3 }
        h.ProofCommitment = cert.ProofCommitment()
        hash := h.BlockHash()
        switch version { case 1: c1.Hash = hash; case 2: c2.Hash = hash; case 3: c3.Hash = hash }
        var header, certified bytes.Buffer
        check(h.Serialize(&header))
        msg := wire.MsgCertificate{Certificate: cert}
        check(msg.PrlEncode(&certified, wire.ProtocolVersion))
        check(h.Serialize(&certified))
        fixtures = append(fixtures, map[string]any{
            "version":version, "header":hex.EncodeToString(header.Bytes()),
            "certifiedHeader":hex.EncodeToString(certified.Bytes()),
            "blockHash": hash.String(),
        })
    }
    enc := json.NewEncoder(os.Stdout)
    enc.SetIndent("", "  ")
    check(enc.Encode(fixtures))
}
