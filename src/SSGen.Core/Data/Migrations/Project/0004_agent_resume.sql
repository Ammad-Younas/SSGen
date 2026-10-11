-- Resume support: the full exploration state of each screen, replayable links, and run lineage.
ALTER TABLE agent_runs ADD COLUMN parent_run_id INTEGER REFERENCES agent_runs(id) ON DELETE SET NULL;

-- JSON: structure, selection, known/tried element keys, revisits, scrolled, root, scrollable.
ALTER TABLE screen_nodes ADD COLUMN state TEXT;

ALTER TABLE screen_edges ADD COLUMN element_key TEXT;
