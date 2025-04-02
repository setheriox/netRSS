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
CREATE TABLE entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    description TEXT,
    link TEXT,
    published DATETIME NOT NULL,
    feed_id INTEGER NOT NULL, read INTEGER DEFAULT 0, filtered INTEGER DEFAULT 0,
    FOREIGN KEY (feed_id) REFERENCES feeds (id)
);
CREATE TABLE filters (
    id INTEGER PRIMARY KEY AUTOINCREMENT, 
    term TEXT NOT NULL, 
    title BOOLEAN DEFAULT 0,
    description BOOLEAN DEFAULT 0,
    UNIQUE(term)
);
CREATE TABLE allowlist (
    entry_id INTEGER PRIMARY KEY,
    FOREIGN KEY (entry_id) REFERENCES entries (id) ON DELETE CASCADE
);