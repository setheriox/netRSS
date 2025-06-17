-- Add ItemsPerPage setting if it doesn't exist
INSERT OR IGNORE INTO settings (key, value, type, description)
VALUES ('items_per_page', '25', 'number', 'Number of items to display per page');
 
-- Add AutoHideRead setting if it doesn't exist
INSERT OR IGNORE INTO settings (key, value, type, description)
VALUES ('auto_hide_read', '0', 'boolean', 'Automatically hide read items when viewing unread items'); 