# Shared daemon RPC error compatibility

The HTTP RpcClient used by all coin families requires an integer error code that
fits its existing Int32 caller contract and a JSON string message. Empty strings
are accepted. Missing/null/numeric messages and coercible/non-integer codes are
structural contract errors, returned with a synthetic -500 and a preserved
JsonSerializationException cause. A valid daemon error retains its original code,
message and data. The BLAKE2b financial gate classifies structural faults as 703.

The official source snapshots below were inspected on
2026-10-05, including serializers rather than relying only on JSON-RPC conventions.
No reviewed serializer omits or nulls a normal error message. No runtime relaxation
or speculative production change is warranted by this audit.

| Implementation and exact source | Error construction and relevant Miningcore behavior |
| --- | --- |
| CryptoNote reference, [Monero f6a591c3](https://github.com/monero-project/monero/blob/f6a591c33a40957800bc0d8cb4804058ab36d1f3/contrib/epee/include/net/jsonrpc_structs.h), [dispatcher](https://github.com/monero-project/monero/blob/f6a591c33a40957800bc0d8cb4804058ab36d1f3/contrib/epee/include/net/http_server_handlers_map2.h) | The epee error stores int64 code and std::string message; both use required KV_SERIALIZE, not its optional/default macro. The dispatcher populates standard error fields. Known daemon and JSON-RPC codes fit Int32; -32601 reaches CryptoNote's transfer_split capability fallback unchanged. The default empty string is valid. |
| [Zano f05767c4](https://github.com/hyle-team/zano/blob/f05767c44367d9427e64b0d81863bf83a40c92ac/contrib/epee/include/net/http_server_handlers_map2.h) | epee likewise serializes both int64 code and string message and fills dispatcher errors. Its error envelope additionally retains method. Known application codes are small negative integers; the -32601 transfer_split fallback remains unchanged. |
| Ethereum reference, [Geth 9bbffb6f](https://github.com/ethereum/go-ethereum/blob/9bbffb6fcdbc1068849b013da448a0e7561d7d4c/rpc/json.go), [error codes](https://github.com/ethereum/go-ethereum/blob/9bbffb6fcdbc1068849b013da448a0e7561d7d4c/rpc/errors.go) | jsonError always serializes integer Code and string Message; only Data is optional. errorMessage sets Message from err.Error() and preserves ErrorCode()/ErrorData(). Subscription support errors use -32601, which Miningcore's Ethereum subscription retry recognizes. |
| [Beam daf71915](https://github.com/BeamMW/beam/blob/daf7191567b746953e6e7a8d72d4396b6d4c94f5/wallet/api/base/api_base.cpp), [message mapping](https://github.com/BeamMW/beam/blob/daf7191567b746953e6e7a8d72d4396b6d4c94f5/wallet/api/base/api_errors_imp.cpp) | The wallet API used by RpcClient always builds code from ApiError and message from getApiErrorMessage(code); optional data is a string. Even an unmapped code has a string fallback. Miningcore's separate Beam REST/explorer/socket transports are outside this HTTP decoder. |
| [Handshake hsd 698e252e lockfile](https://github.com/handshake-org/hsd/blob/698e252ebc7b5c1dd0a9587e342fdd153d020ae4/package-lock.json), [bweb 0.3.0 at 10f922f2](https://github.com/bcoin-org/bweb/blob/10f922f2a4fcbd3845e9e70827d9882e718f55fb/lib/rpc.js) | hsd node and wallet RPC inherit the locked bweb RPC. Standard dispatcher errors include both fields; RPCError asserts number code and string message. Its catch sends err.message and the selected code. hsd's known codes are integers; -5 not-found and -13 wallet-unlock decisions retain their codes. An arbitrary plugin throwing a malformed object does not become a trusted daemon error. |
| [Xelis db59b5c2](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/rpc/error.rs) | Shared daemon/wallet RpcResponseError.to_json always emits i16 code and a formatted string message, plus kind. Missing blocks/invalid parameters use -32602, which Miningcore's block classification recognizes. Extra kind data remains accepted. |

The [JSON-RPC error contract](https://www.jsonrpc.org/specification#error_object)
also requires integer code and string message. Legacy envelopes containing
`result:null` on errors remain supported; JSON-RPC 2.0 errors may omit result.
This audit retains the global strict rule rather than granting a missing-message
exception that could conceal a truncated or contradictory response.

## Regression evidence and scope

DaemonErrorCompatibilityTests sends nine source-shaped offline profiles through the
production RpcClient over an actual local HTTP server, in single and batch decoder
paths (18 cases). Profiles preserve known method-not-found, not-found and
invalid-parameter codes, legacy/null-result and error-only envelopes, empty-string
messages, opaque structured/string data and Xelis kind. Valid errors have no
synthetic inner exception; diagnostic logs must withhold private message/data.
Existing malformed-envelope/703 tests continue to reject absent/null/non-string
messages. The batch wrapper tests the shared decoder, not each daemon's batch support.

Fixtures record immutable source links. Messages/data containing synthetic values
exercise permitted serializer inputs; these are not live captures. Source review
covers the named reference implementations at these snapshots and the locked
Handshake dependency. It does not certify every CryptoNote/Ethereum fork, plugin,
proxy or unreviewed build registered in the coin catalogue. No production wallet
or external payout was contacted. A fork/proxy that violates the error contract
must be reviewed and corrected at its source; code-specific settlement/capability
handling must not be inferred from a malformed error or forced from its message.

Validation counts and their tested source heads are recorded in the
[compatibility validation evidence](bitcoin-blake2b-knots-29.4.2-review.md#validation-evidence).
Current-head CI is recorded on PR #207 and Issue #205. The DATUM #163 and
immediately-before-merge upstream freshness gates remain open.
