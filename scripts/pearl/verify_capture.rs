// Research harness, not a production pool verifier.
// Run as a zk-pow example in the pinned Pearl checkout, with --locked.
use std::{fs::File, io::Read};
use anyhow::{Result, ensure};
use base64::{Engine as _, engine::general_purpose::STANDARD};
use zk_pow::api::{
    proof::{IncompleteBlockHeader, SeedDerivation},
    proof_utils::{CompiledPublicParams, compute_jackpot_hash},
    verify::verify_plain_proof,
};
use zk_pow::circuit::{chip::compute_jackpot, pearl_noise::compute_noise};
use zk_pow::ffi::plain_proof::PlainProof;

fn hex(bytes: &[u8]) -> String { bytes.iter().map(|b| format!("{b:02x}")).collect() }
fn read_json(path: &str) -> Result<serde_json::Value> {
    let mut data = Vec::new();
    File::open(path)?.take(12 * 1024 * 1024 + 1).read_to_end(&mut data)?;
    ensure!(data.len() <= 12 * 1024 * 1024, "capture too large");
    Ok(serde_json::from_slice(&data)?)
}
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    ensure!(args.len() == 3, "usage: verify_capture JOB_JSON SUBMIT_JSON");
    let job = read_json(&args[1])?;
    let submit = read_json(&args[2])?;
    let h = job["params"]["header"].as_str().unwrap();
    ensure!(h.len() == 152, "header size");
    let header_bytes: Vec<u8> = (0..76).map(|i| u8::from_str_radix(&h[i*2..i*2+2],16).unwrap()).collect();
    let header = IncompleteBlockHeader::from_bytes(&header_bytes)?;
    let raw = STANDARD.decode(submit["params"]["plain_proof"].as_str().unwrap())?;
    ensure!(raw.len() <= 8*1024*1024, "proof size");
    let proof = PlainProof::deserialize_compat(&raw)?;
    verify_plain_proof(&header, &proof, None, SeedDerivation::Salted)?;
    let (private, mut public) = proof.parse_proof(header, SeedDerivation::Salted)?;
    let compiled = CompiledPublicParams::from(&public);
    let noise = compute_noise(&compiled);
    let jackpot = compute_jackpot(&compiled, &private.s_a, &private.s_b, &noise);
    public.hash_jackpot = compute_jackpot_hash(&jackpot, compiled.a_noise_seed());
    let mut hash_be = public.hash_jackpot;
    hash_be.reverse();
    println!("{}", serde_json::json!({"valid_under_header_target":true,"version":3,
        "m":proof.m,"n":proof.n,"k":proof.k,"rank":proof.noise_rank,
        "rows":proof.a.row_indices.len(),"columns":proof.bt.row_indices.len(),
        "raw_bytes":raw.len(),"jackpot_be":hex(&hash_be),"job_target":job["params"]["target"],
        "public_data":hex(&public.to_wire_bytes()?)}));
    Ok(())
}
