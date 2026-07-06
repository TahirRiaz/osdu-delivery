//! Persisted MCP configuration and access token.
//!
//! Two files under `~/.sqlflow/`:
//!   * `mcp-config.json` — the control-plane URL.
//!   * `mcp-token.json`  — the bearer token, its scopes, and expiry.
//!
//! The token file is written owner-only where the platform supports it. Both
//! are read at startup and rewritten on change, so a device-auth session
//! survives restarts (matching the DeltaForge MCP token cache).

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
}

impl TokenCache {
    pub fn is_expired(&self, skew_secs: i64) -> bool {
        match self.expires_at {
            Some(exp) => Utc::now() + chrono::Duration::seconds(skew_secs) >= exp,
            None => false,
        }
    }
}

fn sqlflow_dir() -> Option<PathBuf> {
    dirs::home_dir().map(|h| h.join(".sqlflow"))
}

fn config_path() -> Option<PathBuf> {
    sqlflow_dir().map(|d| d.join("mcp-config.json"))
}

fn token_path() -> Option<PathBuf> {
    sqlflow_dir().map(|d| d.join("mcp-token.json"))
}

pub fn load_config() -> McpConfig {
    let Some(path) = config_path() else {
        return McpConfig::default();
    };
    match std::fs::read_to_string(&path) {
        Ok(text) => serde_json::from_str(&text).unwrap_or_default(),
        Err(_) => McpConfig::default(),
    }
}

pub fn save_config(cfg: &McpConfig) -> std::io::Result<()> {
    let Some(path) = config_path() else {
        return Ok(());
    };
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let text = serde_json::to_string_pretty(cfg).unwrap_or_default();
    std::fs::write(path, text)
}

pub fn load_token() -> Option<TokenCache> {
    let path = token_path()?;
    let text = std::fs::read_to_string(&path).ok()?;
    serde_json::from_str(&text).ok()
}

pub fn save_token(token: &TokenCache) -> std::io::Result<()> {
    let Some(path) = token_path() else {
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

pub fn clear_token() -> std::io::Result<()> {
    if let Some(path) = token_path() {
        if path.exists() {
            std::fs::remove_file(path)?;
        }
    }
    Ok(())
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
