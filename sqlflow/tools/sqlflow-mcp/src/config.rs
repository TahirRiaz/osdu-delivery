//! Persisted MCP configuration and access token.
//!
//! Two files under `~/.sqlflow/`:
//!   * `mcp-config.json` — the control-plane URL.
//!   * `mcp-token.json`  — the bearer token, its scopes, and expiry.
//!
//! The token file is written owner-only where the platform supports it. Both
//! are read at startup and rewritten on change, so a device-auth session
//! survives restarts (matching the DeltaForge MCP token cache).
//!
//! The `mcp` in both names is the server's state prefix ([`StateStore`]). A host that
//! composes this server under a name of its own gives it a prefix of its own, so two
//! servers on one machine, signed in to two control planes, never read each other's
//! token.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

pub const DEFAULT_URL: &str = "http://localhost:8080";

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct McpConfig {
    #[serde(rename = "controlPlaneUrl")]
    pub control_plane_url: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TokenCache {
    pub access_token: String,
    #[serde(default)]
    pub scope: String,
    pub expires_at: Option<DateTime<Utc>>,
    /// The catalog id of the personal access token, when this credential is a managed PAT we minted (so we can
    /// revoke it after rotating). Absent for a pasted token or a short-lived device token.
    #[serde(default)]
    pub token_id: Option<String>,
    /// True for a managed PAT the server minted for us: one we rotate on our own before it expires, so the user
    /// never has to sign in again while the client stays in use. A pasted secret or device token is not renewable.
    #[serde(default)]
    pub renewable: bool,
}

impl TokenCache {
    pub fn is_expired(&self, skew_secs: i64) -> bool {
        match self.expires_at {
            Some(exp) => Utc::now() + chrono::Duration::seconds(skew_secs) >= exp,
            None => false,
        }
    }

    /// Whether this managed PAT has entered its rotation window: it is still valid but expires within
    /// <paramref name="window_days"/>, so it should be replaced now while it can still authenticate the mint of its
    /// successor. A non-renewable or already-expired token never qualifies (the former is not ours to rotate, the
    /// latter can no longer mint a replacement and needs a fresh sign-in).
    pub fn should_rotate(&self, window_days: i64) -> bool {
        if !self.renewable || self.token_id.is_none() {
            return false;
        }
        match self.expires_at {
            Some(exp) => {
                let now = Utc::now();
                now < exp && now + chrono::Duration::days(window_days) >= exp
            }
            None => false,
        }
    }
}

fn sqlflow_dir() -> Option<PathBuf> {
    dirs::home_dir().map(|h| h.join(".sqlflow"))
}

/// The prefix SQLFlow's own server keeps its state files under.
pub const DEFAULT_STATE_PREFIX: &str = "mcp";

/// Where one server keeps its control-plane URL and its token: `<prefix>-config.json` and
/// `<prefix>-token.json` under `~/.sqlflow/`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StateStore {
    prefix: String,
}

impl Default for StateStore {
    fn default() -> Self {
        StateStore {
            prefix: DEFAULT_STATE_PREFIX.to_string(),
        }
    }
}

impl StateStore {
    /// A store under `prefix`, which becomes part of two file names: lowercase letters, digits and
    /// hyphens, starting with a letter, at most 64 characters.
    pub fn new(prefix: &str) -> Result<Self, String> {
        let valid = !prefix.is_empty()
            && prefix.len() <= 64
            && prefix.starts_with(|c: char| c.is_ascii_lowercase())
            && prefix
                .chars()
                .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-');
        if !valid {
            return Err(format!(
                "'{prefix}' is not a state prefix: lowercase letters, digits and hyphens, starting with a \
                 letter, at most 64 characters"
            ));
        }
        Ok(StateStore {
            prefix: prefix.to_string(),
        })
    }

    pub fn prefix(&self) -> &str {
        &self.prefix
    }

    fn config_path(&self) -> Option<PathBuf> {
        sqlflow_dir().map(|d| d.join(format!("{}-config.json", self.prefix)))
    }

    fn token_path(&self) -> Option<PathBuf> {
        sqlflow_dir().map(|d| d.join(format!("{}-token.json", self.prefix)))
    }

    pub fn load_config(&self) -> McpConfig {
        let Some(path) = self.config_path() else {
            return McpConfig::default();
        };
        match std::fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str(&text).unwrap_or_default(),
            Err(_) => McpConfig::default(),
        }
    }

    pub fn save_config(&self, cfg: &McpConfig) -> std::io::Result<()> {
        let Some(path) = self.config_path() else {
            return Ok(());
        };
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let text = serde_json::to_string_pretty(cfg).unwrap_or_default();
        std::fs::write(path, text)
    }

    pub fn load_token(&self) -> Option<TokenCache> {
        let path = self.token_path()?;
        let text = std::fs::read_to_string(&path).ok()?;
        serde_json::from_str(&text).ok()
    }

    pub fn save_token(&self, token: &TokenCache) -> std::io::Result<()> {
        let Some(path) = self.token_path() else {
            return Ok(());
        };
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let text = serde_json::to_string_pretty(token).unwrap_or_default();
        std::fs::write(&path, text)?;
        restrict_permissions(&path);
        Ok(())
    }

    pub fn clear_token(&self) -> std::io::Result<()> {
        if let Some(path) = self.token_path() {
            if path.exists() {
                std::fs::remove_file(path)?;
            }
        }
        Ok(())
    }
}

#[cfg(unix)]
fn restrict_permissions(path: &std::path::Path) {
    use std::os::unix::fs::PermissionsExt;
    let _ = std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600));
}

#[cfg(not(unix))]
fn restrict_permissions(_path: &std::path::Path) {
    // On Windows the file inherits the user profile ACL; no extra action.
}

#[cfg(test)]
mod tests {
    use super::*;

    fn token(renewable: bool, id: Option<&str>, expires_in_days: Option<i64>) -> TokenCache {
        TokenCache {
            access_token: "sqlf_x".to_string(),
            scope: "read operate".to_string(),
            expires_at: expires_in_days.map(|d| Utc::now() + chrono::Duration::days(d)),
            token_id: id.map(|s| s.to_string()),
            renewable,
        }
    }

    #[test]
    fn rotates_only_a_renewable_token_inside_its_window() {
        // Managed, expiring in 5 days: inside the 14-day window -> rotate.
        assert!(token(true, Some("id"), Some(5)).should_rotate(14));
    }

    #[test]
    fn does_not_rotate_a_token_with_ample_life() {
        // Managed but 60 days out: well outside the window -> leave it.
        assert!(!token(true, Some("id"), Some(60)).should_rotate(14));
    }

    #[test]
    fn does_not_rotate_a_pasted_or_device_token() {
        // Not renewable (a pasted secret / device token), even inside the window: not ours to rotate.
        assert!(!token(false, None, Some(1)).should_rotate(14));
        // Renewable in shape but missing an id (cannot be revoked) is likewise skipped.
        assert!(!token(true, None, Some(1)).should_rotate(14));
    }

    #[test]
    fn does_not_rotate_an_already_expired_token() {
        // Past expiry: a mint would 401, so this needs a fresh sign-in, not a rotation.
        assert!(!token(true, Some("id"), Some(-1)).should_rotate(14));
    }

    #[test]
    fn never_expiring_managed_token_is_not_rotated() {
        assert!(!token(true, Some("id"), None).should_rotate(14));
    }

    #[test]
    fn the_default_store_keeps_the_file_names_it_always_had() {
        // An upgrade must find the sign-in an earlier build stored.
        let store = StateStore::default();
        let config = store.config_path().expect("a home directory");
        let token = store.token_path().expect("a home directory");
        assert_eq!(config.file_name().unwrap(), "mcp-config.json");
        assert_eq!(token.file_name().unwrap(), "mcp-token.json");
    }

    #[test]
    fn a_host_prefix_names_files_of_its_own() {
        let store = StateStore::new("probe-mcp").expect("a valid prefix");
        assert_eq!(store.config_path().unwrap().file_name().unwrap(), "probe-mcp-config.json");
        assert_eq!(store.token_path().unwrap().file_name().unwrap(), "probe-mcp-token.json");
        assert_ne!(store.token_path(), StateStore::default().token_path());
    }

    #[test]
    fn a_prefix_that_could_leave_the_state_folder_is_refused() {
        for bad in ["", "../etc", "a/b", "A", "1mcp", "mcp token", "mcp.json"] {
            let refused = StateStore::new(bad).expect_err(bad);
            assert!(refused.contains("not a state prefix"), "{refused}");
        }
        assert!(StateStore::new(&"a".repeat(65)).is_err());
    }
}
