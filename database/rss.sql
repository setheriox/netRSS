CREATE TABLE categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    color TEXT NOT NULL
);
CREATE TABLE sqlite_sequence(name,seq);
CREATE TABLE feeds (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    url TEXT NOT NULL,
    category_id INTEGER NOT NULL,
    FOREIGN KEY (category_id) REFERENCES categories (id)
);
CREATE TABLE feed_status (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    feed_id INTEGER NOT NULL,
    status TEXT DEFAULT 'ok',
    error_message TEXT,
    last_checked DATETIME DEFAULT CURRENT_TIMESTAMP,
    fail_count INTEGER DEFAULT 0,
    is_critical BOOLEAN DEFAULT 0,
    FOREIGN KEY (feed_id) REFERENCES feeds (id) ON DELETE CASCADE
);
CREATE TABLE entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    description TEXT,
    link TEXT,
    published DATETIME NOT NULL,
    feed_id INTEGER NOT NULL, 
    read INTEGER DEFAULT 0, 
    filtered INTEGER DEFAULT 0,
    starred INTEGER DEFAULT 0,
    manually_filtered INTEGER DEFAULT 0,
    filter_reason TEXT DEFAULT NULL,
    FOREIGN KEY (feed_id) REFERENCES feeds (id)
);
CREATE TABLE filters (
    id INTEGER PRIMARY KEY AUTOINCREMENT, 
    term TEXT NOT NULL, 
    title BOOLEAN DEFAULT 0,
    description BOOLEAN DEFAULT 0,
    display_term TEXT,
    UNIQUE(term)
);
CREATE TABLE allowlist (
    entry_id INTEGER PRIMARY KEY,
    FOREIGN KEY (entry_id) REFERENCES entries (id) ON DELETE CASCADE
);
CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    type TEXT DEFAULT 'string',
    description TEXT
);

-- Insert default settings
INSERT INTO settings (key, value, type, description) 
VALUES ('font_family', 'Verdana, sans-serif', 'string', 'Font family for the application');

INSERT INTO settings (key, value, type, description) 
VALUES ('font_size', '8', 'number', 'Base font size in points'); 