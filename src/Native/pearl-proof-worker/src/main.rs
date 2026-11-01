// One request per process: circuit allocations and panics cannot survive a job.
// Consensus dependency: pearl d7c517d267f10f5bcbbdc366f3e603942f9a5ab5.
use std::io::{Read, Write};
use anyhow::{Result, ensure};
use bincode::Options;
use primitive_types::U256;
use zk_pow::api::{
    proof::{IncompleteBlockHeader, SeedDerivation},
    proof_utils::{CompiledPublicParams, compute_jackpot_hash},
    sanity_checks::check_rank_penalty,
    verify::{verify_plain_proof, verify_block},
    prove::prove_block,
};
use zk_pow::circuit::{
    chip::compute_jackpot, pearl_noise::compute_noise,
    circuit_utils::CircuitCache,
};
use zk_pow::ffi::plain_proof::PlainProof;

const MAX_PROOF: usize = 8 * 1024 * 1024;
const SOURCE: &str = "d7c517d267f10f5bcbbdc366f3e603942f9a5ab5";

fn main() {
    // stdout is exclusively the bounded binary response.
    std::panic::set_hook(Box::new(|_| {}));
    if std::env::args().nth(1).as_deref() == Some("--version") {
        println!("miningcore-pearl-worker 1 {SOURCE} linux-x64");
        return;
    }
    let result = std::panic::catch_unwind(run);
    if !matches!(result, Ok(Ok(()))) {
        let _ = std::io::stdout().write_all(b"PRLRES01\x01\x00\x00\x00");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    ensure!(cfg!(all(target_os = "linux", target_arch = "x86_64")), "unsupported platform");
    // Hard limits apply even when the .NET caller times out or disappears.
    #[cfg(target_os = "linux")]
    unsafe {
        let parent = libc::getppid();
        ensure!(libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGKILL) == 0, "parent lifetime");
        ensure!(libc::getppid() == parent && parent != 1, "parent exited");
        let memory = libc::rlimit { rlim_cur: 8 * 1024 * 1024 * 1024, rlim_max: 8 * 1024 * 1024 * 1024 };
        let cpu = libc::rlimit { rlim_cur: 600, rlim_max: 600 };
        ensure!(libc::setrlimit(libc::RLIMIT_AS, &memory) == 0, "memory limit");
        ensure!(libc::setrlimit(libc::RLIMIT_CPU, &cpu) == 0, "cpu limit");
    }
    let mut input = std::io::stdin().lock();
    let mut fixed = [0u8; 124];
    input.read_exact(&mut fixed)?;
    ensure!(&fixed[..8] == b"PRLJOB01", "protocol");
    ensure!(fixed[8] <= 1 && fixed[9] == 3 && fixed[10..12] == [0, 0], "unsupported operation");
    #[cfg(target_os = "linux")]
    if fixed[8] == 0 {
        unsafe {
            let memory = libc::rlimit { rlim_cur: 1024 * 1024 * 1024, rlim_max: 1024 * 1024 * 1024 };
            let cpu = libc::rlimit { rlim_cur: 20, rlim_max: 20 };
            ensure!(libc::setrlimit(libc::RLIMIT_AS, &memory) == 0, "verification memory limit");
            ensure!(libc::setrlimit(libc::RLIMIT_CPU, &cpu) == 0, "verification CPU limit");
        }
    }
    let header = IncompleteBlockHeader::from_bytes(&fixed[12..88])?;
    let target = U256::from_big_endian(&fixed[88..120]);
    ensure!(!target.is_zero(), "zero target");
    let size = u32::from_le_bytes(fixed[120..124].try_into()?) as usize;
    ensure!((32..=MAX_PROOF).contains(&size), "proof size");
    let mut raw = vec![0u8; size];
    input.read_exact(&mut raw)?;
    let mut trailing = [0u8; 1];
    ensure!(input.read(&mut trailing)? == 0, "trailing request");
    let canonical_len = preflight(&raw)?;
    raw.truncate(canonical_len);
    raw.push(0);
    let proof: PlainProof = bincode::options().with_fixint_encoding()
        .with_limit(MAX_PROOF as u64).reject_trailing_bytes().deserialize(&raw)?;

    // The official routine checks all Merkle/matrix/noise/rank constraints.
    // A saturated target here removes only the jackpot threshold; exact thresholds follow.
    verify_plain_proof(&header, &proof, Some(0x207fffff), SeedDerivation::Salted)?;
    let (private, mut public) = proof.parse_proof(header, SeedDerivation::Salted)?;
    public.sanity_check()?;
    let compiled = CompiledPublicParams::from(&public);
    let noise = compute_noise(&compiled);
    let jackpot = compute_jackpot(&compiled, &private.s_a, &private.s_b, &noise);
    public.hash_jackpot = compute_jackpot_hash(&jackpot, compiled.a_noise_seed());
    let length = proof.k - proof.k % proof.noise_rank;
    let factor = proof.a.row_indices.len() as u64 * proof.bt.row_indices.len() as u64
        * (length / proof.noise_rank) as u64 * 128;
    let (bound, overflow) = target.overflowing_mul(U256::from(factor));
    ensure!(!overflow, "trivial share bound");
    ensure!(U256::from_little_endian(&public.hash_jackpot) <= bound, "low difficulty");
    let candidate = verify_plain_proof(&header, &proof, None, SeedDerivation::Salted).is_ok();
    let mut certificate = Vec::new();
    if fixed[8] == 1 {
        ensure!(candidate, "not a block candidate");
        // A fresh bounded cache per process cannot grow across attacker-chosen jobs.
        let mut cache = CircuitCache::from_bytes(zk_pow::circuit::embedded_cache::CACHE_DATA)?;
        let zk = prove_block(&mut public, private, &mut cache)?;
        verify_block(&public, &zk, &mut cache)?;
        check_rank_penalty(&public.mining_config, &public.hash_jackpot, header.nbits)?;
        let (data, bytes) = zk.serialize(&public)?;
        ensure!(data.len() == 164 && (1..=60000).contains(&bytes.len()), "certificate size");
        certificate = bytes;
    }
    let data = public.to_wire_bytes()?;
    ensure!(data.len() == 164, "dense public data size");
    let mut output = Vec::with_capacity(180 + certificate.len());
    output.extend_from_slice(b"PRLRES01");
    output.extend_from_slice(&[0, candidate as u8, 0, 0]);
    output.extend_from_slice(&data);
    output.extend_from_slice(&(certificate.len() as u32).to_le_bytes());
    output.extend_from_slice(&certificate);
    std::io::stdout().write_all(&output)?;
    Ok(())
}

// Walk every nested count before serde can allocate. Mirrors the deployed bincode layout,
// including length prefixes on each 1024-byte leaf. MoE and trailing bytes fail closed.
fn preflight(raw: &[u8]) -> Result<usize> {
    struct Reader<'a> { raw: &'a [u8], offset: usize }
    impl Reader<'_> {
        fn skip(&mut self, count: usize) -> Result<()> {
            ensure!(count <= self.raw.len() - self.offset, "truncated");
            self.offset += count;
            Ok(())
        }
        fn number(&mut self, min: u64, max: u64) -> Result<u64> {
            ensure!(self.raw.len() - self.offset >= 8, "truncated integer");
            let n = u64::from_le_bytes(self.raw[self.offset..self.offset+8].try_into()?);
            self.offset += 8;
            ensure!((min..=max).contains(&n), "count bounds");
            Ok(n)
        }
        fn indices(&mut self, count: u64, limit: u64) -> Result<()> {
            let mut previous = 0;
            for i in 0..count {
                let next = self.number(0, limit-1)?;
                ensure!(i == 0 || next > previous, "index order");
                previous = next;
            }
            Ok(())
        }
        fn matrix(&mut self, dimension: u64, k: u64) -> Result<u64> {
            let leaves = self.number(1, (MAX_PROOF / 1032) as u64)?;
            ensure!(leaves <= ((self.raw.len() - self.offset) / 1032) as u64, "leaf bounds");
            for _ in 0..leaves { self.number(1024, 1024)?; self.skip(1024)?; }
            self.number(leaves, leaves)?;
            let total = (dimension*k + 1023) / 1024;
            self.indices(leaves, total)?;
            self.number(total, total)?;
            self.skip(32)?;
            let siblings = self.number(0, (leaves*30).min((MAX_PROOF/32) as u64))?;
            self.skip(siblings as usize * 32)?;
            let rows = self.number(1, 128)?;
            self.indices(rows, dimension)?;
            Ok(rows)
        }
    }
    ensure!(raw.len() <= MAX_PROOF, "proof size");
    let mut r = Reader { raw, offset: 0 };
    let m = r.number(1, 1<<24)?;
    let n = r.number(1, 1<<24)?;
    let k = r.number(1024, 1<<16)?;
    let rank = r.number(128, 1024)?;
    ensure!(rank.is_power_of_two() && k%64 == 0 && k>=16*rank && k<=4*rank*rank, "dimensions");
    let rows = r.matrix(m,k)?;
    let cols = r.matrix(n,k)?;
    ensure!((32..=256).contains(&(rows*cols)) && (rows+cols)*(k-k%rank)<=1<<22, "tile bounds");
    ensure!(raw.len()==r.offset || raw.len()==r.offset+1 && raw[r.offset]==0, "dense format");
    Ok(r.offset)
}
