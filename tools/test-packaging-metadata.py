"""Historical execution cannot silently qualify changed runtime/build properties."""
import hashlib
from pathlib import Path
import runpy
import unittest
matches=runpy.run_path(str(Path(__file__).with_name('device-extraction-index.py')))['packaging_metadata_matches']
def sha(data):return hashlib.sha256(data).hexdigest()
class MetadataProof(unittest.TestCase):
 def setUp(self):
  self.old=b'<Project><Version>0.2</Version><PackageProjectUrl>old</PackageProjectUrl><IsAotCompatible>true</IsAotCompatible></Project>'
  self.new=self.old.replace(b'>0.2<',b'>0.3<').replace(b'>old<',b'>new<')
  self.row=dict(qualifiedSourceSha256=sha(self.old),candidateSourceSha256=sha(self.new),metadataReplacements=[dict(element='Version',fromValue='0.2',toValue='0.3'),dict(element='PackageProjectUrl',fromValue='old',toValue='new')])
 def test_exact_allowed_package_metadata_preserves_historical_identity(self):self.assertTrue(matches(self.new,self.row,sha(self.old)))
 def test_runtime_change_fails_even_if_candidate_hash_is_updated(self):
  changed=self.new.replace(b'>true<',b'>false<');self.row['candidateSourceSha256']=sha(changed)
  self.assertFalse(matches(changed,self.row,sha(self.old)))
 def test_runtime_property_cannot_be_declared_package_metadata(self):
  changed=self.new.replace(b'>true<',b'>false<');self.row['candidateSourceSha256']=sha(changed);self.row['metadataReplacements'].append(dict(element='IsAotCompatible',fromValue='true',toValue='false'))
  self.assertFalse(matches(changed,self.row,sha(self.old)))
if __name__=='__main__':unittest.main()
